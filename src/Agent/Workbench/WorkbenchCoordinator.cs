using Agent.Mcp;
using Contracts.Engineering;
using Contracts.Knowledge;
using Contracts.Sandbox;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Agent.Workbench;

public sealed class WorkbenchLifecycleException : Exception
{
    public WorkbenchLifecycleException(string code, string message)
        : base($"{code}: {message}") => Code = code;

    public string Code { get; }
}

public sealed record CreateWorkbenchRequest(
    string Name,
    string? RootPath,
    int? EngineeringSessionId,
    string? EngineeringProjectPath);

public sealed record CreateWorkbenchResult(
    WorkbenchMetadata Workbench,
    WorktreeMetadata Worktree,
    IReadOnlyList<DeviceMetadata> Devices);

public sealed record CreateWorktreeRequest(
    WorkbenchMetadata Workbench,
    string Name,
    string Branch,
    string? StartPoint = null);

public sealed record ApprovedReconciliation(
    ReconciliationPreview Preview,
    IReadOnlySet<string> ApprovedPaths,
    bool Approved = true)
{
    public static ApprovedReconciliation Rejected(ReconciliationPreview preview) =>
        new(preview, new HashSet<string>(StringComparer.Ordinal), false);
}

public enum RefreshApplyState
{
    Rejected,
    Committed,
    FilesUpdatedCommitFailed,
}

public sealed record RefreshApplyResult(
    RefreshApplyState State,
    IReadOnlyList<string> ChangedPaths,
    string? CommitSha,
    string? Error);

public sealed record DeviceBootstrapResult(
    RefreshApplyResult Baseline,
    KnowledgeUpdateResult Knowledge);

public sealed record ImportModifiedResult(
    string RelativePath,
    bool ImportSucceeded,
    string CompileState,
    IReadOnlyList<string> Warnings,
    string? Error);

public sealed record CoordinatorGitCommitResult
{
    public string Sha { get; init; } = string.Empty;
}

/// <summary>
/// Coordinates device-scoped mutations across MCP boundaries. No caller may bypass the
/// staging/approval boundary when refreshing a tracked baseline.
/// </summary>
public sealed class WorkbenchCoordinator
{
    private readonly IMcpToolCaller engineering;
    private readonly IMcpToolCaller knowledge;
    private readonly IMcpToolCaller versionControl;
    private readonly WorkbenchCatalog catalog;
    private readonly AtomicJsonStore store;
    private readonly DeviceReconciler reconciler;
    private readonly DeviceSourceResolver sourceResolver;
    private readonly DeviceOperationLock operationLock;
    private readonly SafeDeviceExportStager stager;
    private readonly PathJail? pathJail;
    private readonly SemaphoreSlim engineeringSession = new(1, 1);
    private readonly ConcurrentDictionary<string, WorkbenchMetadata> knownWorkbenches =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorktreeMetadata> knownWorktrees =
        new(StringComparer.Ordinal);

    public WorkbenchCoordinator(
        IMcpToolCaller engineering,
        IMcpToolCaller knowledge,
        IMcpToolCaller versionControl,
        WorkbenchCatalog catalog,
        AtomicJsonStore store,
        DeviceReconciler reconciler,
        DeviceSourceResolver sourceResolver,
        DeviceOperationLock? operationLock = null,
        PathJail? pathJail = null)
    {
        this.engineering = engineering ?? throw new ArgumentNullException(nameof(engineering));
        this.knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));
        this.versionControl = versionControl ?? throw new ArgumentNullException(nameof(versionControl));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        this.sourceResolver = sourceResolver ?? throw new ArgumentNullException(nameof(sourceResolver));
        this.operationLock = operationLock ?? new DeviceOperationLock();
        this.pathJail = pathJail;
        stager = new SafeDeviceExportStager(engineering, this.operationLock);
    }

    public async Task OpenProjectInTiaAsync(
        DeviceContext device,
        CancellationToken cancellationToken = default,
        IOperationProgress? progress = null,
        bool withUI = true)
    {
        ArgumentNullException.ThrowIfNull(device);
        var worktree = store.Read<WorktreeMetadata>(
            Path.Combine(device.WorktreeRoot, "worktree.json"));
        if (string.IsNullOrWhiteSpace(worktree.SourceProjectPath))
        {
            throw new WorkbenchCatalogException(
                "ENGINEERING_PROJECT_PATH_MISSING",
                $"No engineering project path is registered for worktree '{device.WorktreeId}'.");
        }

        progress?.Report("Opening registered project in TIA Portal...");
        await engineeringSession.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await engineering.CallAsync<object>(
                "connect",
                new { projectPath = worktree.SourceProjectPath, withUI },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            engineeringSession.Release();
        }
    }

    /// <summary>
    /// Re-attaches a still-running TIA instance (by session id) that already holds the
    /// registered project, e.g. after this application was restarted.
    /// </summary>
    public async Task AttachTiaInstanceAsync(
        int sessionId,
        CancellationToken cancellationToken = default,
        IOperationProgress? progress = null)
    {
        progress?.Report("Attaching to running TIA Portal instance...");
        await engineeringSession.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await engineering.CallAsync<object>(
                "connect",
                new { sessionId },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            engineeringSession.Release();
        }
    }

    public async Task<CreateWorkbenchResult> CreateWorkbenchAsync(
        CreateWorkbenchRequest request,
        CancellationToken cancellationToken = default,
        IOperationProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hasSession = request.EngineeringSessionId is not null;
        var hasProjectPath = !string.IsNullOrWhiteSpace(request.EngineeringProjectPath);
        if (hasSession == hasProjectPath)
        {
            throw new WorkbenchLifecycleException(
                "ENGINEERING_CONNECTION_INVALID",
                "Provide exactly one of an engineering session ID or an engineering project path.");
        }

        // Jail the engineering project path BEFORE any workbench artifact is created, so a
        // project outside the sandbox fails here instead of later at tia/open time.
        var validatedProjectPath = hasProjectPath && pathJail is not null
            ? pathJail.Validate(request.EngineeringProjectPath!, "engineeringProjectPath")
            : request.EngineeringProjectPath?.Trim();
        progress?.Report("Preparing workbench storage...");
        var workbench = catalog.Create(request.Name, request.RootPath);
        var masterPath = Path.Combine(workbench.RootPath, "worktrees", "master");

        try
        {
            progress?.Report("Initializing Git repository...");
            await versionControl.CallAsync<object>(
                "vc_init_shared",
                new { workbenchRoot = workbench.RootPath, masterWorktreePath = masterPath },
                cancellationToken).ConfigureAwait(false);
            ProjectInfo project;
            await engineeringSession.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                progress?.Report(hasSession ? "Attaching to TIA Portal..." : "Opening project in TIA Portal...");
                await engineering.CallAsync<object>(
                    "connect",
                    hasSession
                        ? (object)new { sessionId = request.EngineeringSessionId!.Value }
                        : new { projectPath = validatedProjectPath, withUI = true },
                    cancellationToken).ConfigureAwait(false);
                progress?.Report("Discovering PLC devices...");
                project = await engineering.CallAsync<ProjectInfo>(
                    "get_project_info",
                    new { },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                engineeringSession.Release();
            }

            var sourceProjectPath = project.Path ?? validatedProjectPath;
            if (sourceProjectPath is not null
                && pathJail is not null
                && !string.Equals(sourceProjectPath, validatedProjectPath, StringComparison.OrdinalIgnoreCase))
            {
                sourceProjectPath = pathJail.Validate(sourceProjectPath, "engineeringProjectPath");
            }

            progress?.Report("Creating device folders...");
            var worktreeId = Guid.NewGuid().ToString("N");
            var registration = new WorkbenchWorktreeRegistration(
                worktreeId, "master", "master", "master");
            workbench = catalog.RegisterWorktree(
                workbench with
                {
                    EngineeringProjectId = ProjectIdentity(project),
                    SourceProjectPath = sourceProjectPath,
                },
                registration);
            // RegisterWorktree writes the supplied updated workbench.
            var worktree = new WorktreeMetadata(
                WorkbenchSchema.CurrentVersion,
                worktreeId,
                workbench.WorkbenchId,
                "master",
                "master",
                DateTimeOffset.UtcNow.ToString("O"),
                null,
                workbench.EngineeringProjectId,
                workbench.SourceProjectPath,
                Array.Empty<string>(),
                null);

            var devices = project.PlcDevices.Select(plcName =>
                new DeviceMetadata(
                    WorkbenchSchema.CurrentVersion,
                    Guid.NewGuid().ToString("N"),
                    worktreeId,
                    plcName,
                    plcName,
                    null,
                    null,
                    null,
                    new KnowledgeState(
                        true,
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        null,
                        BaselineStale: true),
                    Array.Empty<DeviceImportRecord>()))
                .ToArray();
            worktree = worktree with { DeviceIds = devices.Select(device => device.DeviceId).ToArray() };
            store.Write(Path.Combine(masterPath, "worktree.json"), worktree);
            foreach (var device in devices)
            {
                var context = catalog.ResolveDevice(workbench, worktree, device);
                Directory.CreateDirectory(context.ExportedSourceRoot);
                Directory.CreateDirectory(context.ModifiedSourceRoot);
                Directory.CreateDirectory(context.StagingRoot);
                WriteDevice(context, device);
            }

            RegisterWorkbench(workbench);
            return new CreateWorkbenchResult(workbench, worktree, devices);
        }
        catch
        {
            catalog.RollbackCreate(workbench);
            throw;
        }
    }

    public async Task<WorktreeMetadata> CreateWorktreeAsync(
        CreateWorktreeRequest request,
        CancellationToken cancellationToken = default,
        IOperationProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var relativePath = SafeDirectoryName(request.Name);
        var worktreePath = WorkbenchPaths.ResolveWorktree(
            request.Workbench.RootPath,
            relativePath);
        progress?.Report("Creating linked worktree...");
        await versionControl.CallAsync<object>(
            "vc_add_worktree",
            new
            {
                repositoryPath = request.Workbench.RepositoryPath,
                worktreePath,
                branchName = request.Branch,
                startPoint = request.StartPoint,
            },
            cancellationToken).ConfigureAwait(false);

        progress?.Report("Writing worktree metadata...");
        var inheritedDevices = LoadInheritedDevices(worktreePath, worktreeId: null);
        var worktree = new WorktreeMetadata(
            WorkbenchSchema.CurrentVersion,
            Guid.NewGuid().ToString("N"),
            request.Workbench.WorkbenchId,
            request.Name,
            request.Branch,
            DateTimeOffset.UtcNow.ToString("O"),
            request.StartPoint,
            request.Workbench.EngineeringProjectId,
            request.Workbench.SourceProjectPath,
            inheritedDevices.Select(device => device.DeviceId).ToArray(),
            null);
        store.Write(Path.Combine(worktreePath, "worktree.json"), worktree);
        foreach (var inherited in inheritedDevices)
        {
            var updated = inherited with { WorktreeId = worktree.WorktreeId };
            store.Write(
                Path.Combine(
                    worktreePath,
                    "devices",
                    SafeDirectoryName(updated.PlcName),
                    "device.json"),
                updated);
        }

        var updatedWorkbench = catalog.RegisterWorktree(
            request.Workbench,
            new WorkbenchWorktreeRegistration(
                worktree.WorktreeId,
                request.Name,
                request.Branch,
                relativePath));
        knownWorkbenches[updatedWorkbench.WorkbenchId] = updatedWorkbench;
        knownWorktrees[worktree.WorktreeId] = worktree;
        return worktree;
    }

    public async Task DeleteWorktreeAsync(
        WorkbenchMetadata workbench,
        string worktreeId,
        CancellationToken cancellationToken = default,
        IOperationProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        var persisted = catalog.Load(workbench.RootPath);
        if (!string.Equals(persisted.WorkbenchId, workbench.WorkbenchId, StringComparison.Ordinal))
        {
            throw new WorkbenchCatalogException(
                "WORKBENCH_RELATIONSHIP_MISMATCH",
                "Workbench metadata does not match the persisted catalog entry.");
        }

        var registration = persisted.Worktrees.SingleOrDefault(candidate =>
            string.Equals(candidate.WorktreeId, worktreeId, StringComparison.Ordinal));
        if (registration is null)
        {
            throw new WorkbenchCatalogException(
                "WORKTREE_NOT_FOUND",
                $"Workbench '{persisted.WorkbenchId}' does not contain worktree '{worktreeId}'.");
        }

        if (string.Equals(registration.Branch, "master", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkbenchLifecycleException(
                "MASTER_WORKTREE_PROTECTED",
                "The master worktree is the workbench baseline and cannot be removed.");
        }

        var worktreeRoot = WorkbenchPaths.ResolveWorktree(persisted.RootPath, registration.RelativePath);
        progress?.Report($"Removing linked worktree '{registration.Name}'...");
        try
        {
            await versionControl.CallAsync<object>(
                "vc_remove_worktree",
                new { repositoryPath = persisted.RepositoryPath, worktreePath = worktreeRoot },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ToolCallException exception) when (
            !Directory.Exists(worktreeRoot)
            || IsMissingWorktreeRegistration(exception))
        {
            // A checkout that is already gone, or whose Git registration is stale,
            // cannot be removed by git; the catalog cleanup below finishes the removal.
        }

        var updated = catalog.RemoveWorktree(persisted, worktreeId);
        knownWorkbenches[updated.WorkbenchId] = updated;
        knownWorktrees.TryRemove(worktreeId, out _);
    }

    /// <summary>
    /// Registers a persisted workbench with this coordinator after application selection or
    /// restart, so ID-only merge requests can be resolved without deriving paths from names.
    /// </summary>
    public void RegisterWorkbench(WorkbenchMetadata workbench)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        knownWorkbenches[workbench.WorkbenchId] = workbench;
        foreach (var registration in workbench.Worktrees)
        {
            var root = WorkbenchPaths.ResolveWorktree(
                workbench.RootPath,
                registration.RelativePath);
            var metadataPath = Path.Combine(root, "worktree.json");
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            var metadata = store.Read<WorktreeMetadata>(metadataPath);
            if (!string.Equals(metadata.WorkbenchId, workbench.WorkbenchId, StringComparison.Ordinal)
                || !string.Equals(metadata.WorktreeId, registration.WorktreeId, StringComparison.Ordinal))
            {
                throw new WorkbenchCatalogException(
                    "WORKBENCH_RELATIONSHIP_MISMATCH",
                    $"Worktree metadata at '{metadataPath}' does not match its registration.");
            }

            knownWorktrees[metadata.WorktreeId] = metadata;
        }
    }

    public async Task<SyncResult[]> StageRefreshAsync(
        DeviceContext device,
        CancellationToken token,
        IOperationProgress? progress = null,
        bool allowCompile = false)
    {
        var metadata = ReadDevice(device);
        await engineeringSession.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await StageRefreshCoreAsync(device, metadata.PlcName, token, progress, allowCompile)
                .ConfigureAwait(false);
        }
        finally
        {
            engineeringSession.Release();
        }
    }

    private async Task<SyncResult[]> StageRefreshCoreAsync(
        DeviceContext device,
        string plcName,
        CancellationToken token,
        IOperationProgress? progress,
        bool allowCompile)
    {
        var result = await stager.StageAsync(device, plcName, token, progress, allowCompile).ConfigureAwait(false);
        progress?.Report("Preparing refresh preview...");
        return result;
    }

    public async Task<HardwareConfigurationReloadResult> ReloadHardwareAsync(
        DeviceContext device,
        CancellationToken token,
        IOperationProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        await engineeringSession.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await operationLock.RunAsync(
                device,
                async cancellationToken =>
                {
                    await EnsureActiveProjectMatchesWorktreeAsync(device, cancellationToken, progress)
                        .ConfigureAwait(false);
                    var root = WorkbenchPaths.ResolveHardwareRoot(device.WorktreeRoot);
                    Directory.CreateDirectory(root);
                    progress?.Report("Exporting hardware configuration from TIA...");
                    var results = await engineering.CallAsync<HardwareExportResult[]>(
                        "export_hardware_configuration",
                        new { outputDir = root, includeDeviceExports = true },
                        cancellationToken).ConfigureAwait(false);
                    var warnings = EnsureHardwareExportSucceeded(results, root);
                    RemoveLegacyHardwareLayout(root);

                    var paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Where(path => !IsUnderHardwareStaging(root, path))
                        .Select(path => Path.GetRelativePath(device.WorktreeRoot, path).Replace('\\', '/'))
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToArray();
                    progress?.Report("Committing hardware configuration...");
                    await versionControl.CallAsync<object>(
                        "vc_add",
                        new { repoPath = device.WorktreeRoot, paths },
                        cancellationToken).ConfigureAwait(false);
                    var commit = await versionControl.CallAsync<CoordinatorGitCommitResult>(
                        "vc_commit",
                        new { repoPath = device.WorktreeRoot, message = "hardware: reload configuration" },
                        cancellationToken).ConfigureAwait(false);

                    return new HardwareConfigurationReloadResult(
                        root,
                        results.Count(result => result.Success),
                        results.Count(result => result.Success && result.Scope == "device"),
                        commit.Sha,
                        warnings);
                },
                token).ConfigureAwait(false);
        }
        finally
        {
            engineeringSession.Release();
        }
    }

    public async Task<HardwareConfigurationCompareResult> CompareHardwareAsync(
        DeviceContext device,
        CancellationToken token,
        IOperationProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        await engineeringSession.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await operationLock.RunAsync(
                device,
                async cancellationToken =>
                {
                    await EnsureActiveProjectMatchesWorktreeAsync(device, cancellationToken, progress)
                        .ConfigureAwait(false);
                    var root = WorkbenchPaths.ResolveHardwareRoot(device.WorktreeRoot);
                    var stagingRoot = WorkbenchPaths.ResolveHardwareStagingRoot(device.WorktreeRoot);
                    TryDeleteDirectory(stagingRoot);
                    Directory.CreateDirectory(stagingRoot);
                    progress?.Report("Comparing hardware configuration with TIA...");
                    var liveResults = await engineering.CallAsync<HardwareExportResult[]>(
                        "export_hardware_configuration",
                        new { outputDir = stagingRoot, includeDeviceExports = true },
                        cancellationToken).ConfigureAwait(false);
                    var warnings = EnsureHardwareExportSucceeded(liveResults, stagingRoot);

                    var local = ReadHardwareSnapshot(root);
                    var live = HardwareSnapshot.FromResults(liveResults, stagingRoot);
                    var artifacts = HardwareSnapshot.Compare(local, live);
                    var state = artifacts.All(artifact => artifact.State == "same")
                        ? "in-sync"
                        : local is null
                            ? "missing"
                            : "changed";
                    var changed = artifacts.Count(artifact => artifact.State != "same");
                    var message = state switch
                    {
                        "in-sync" => $"Hardware configuration matches TIA ({artifacts.Count} artifact(s)).",
                        "missing" => "No saved project-level hardware configuration exists yet. Review the staged TIA export before overwriting the baseline.",
                        _ => $"Hardware configuration differs from TIA ({changed} artifact(s) changed or missing). Review the staged TIA export before overwriting the baseline.",
                    };
                    if (warnings.Count > 0)
                    {
                        message += $" CAx export warnings: {string.Join("; ", warnings)}";
                    }
                    return new HardwareConfigurationCompareResult(
                        state,
                        root,
                        artifacts,
                        message,
                        warnings,
                        stagingRoot);
                },
                token).ConfigureAwait(false);
        }
        finally
        {
            engineeringSession.Release();
        }
    }

    public Task<HardwareConfigurationOverwriteResult> OverwriteHardwareFromStagingAsync(
        DeviceContext device,
        bool confirmOverwrite,
        CancellationToken token,
        IOperationProgress? progress = null) =>
        operationLock.RunAsync(
            device,
            async cancellationToken =>
            {
                if (!confirmOverwrite)
                {
                    throw new WorkbenchLifecycleException(
                        "HARDWARE_OVERWRITE_CONFIRMATION_REQUIRED",
                        "Overwriting the saved hardware configuration requires explicit confirmation.");
                }

                var root = WorkbenchPaths.ResolveHardwareRoot(device.WorktreeRoot);
                var stagingRoot = WorkbenchPaths.ResolveHardwareStagingRoot(device.WorktreeRoot);
                var stagedProjectAml = Path.Combine(stagingRoot, "project.aml");
                if (!IsUsableProjectAml(stagedProjectAml))
                {
                    throw new WorkbenchLifecycleException(
                        "HARDWARE_STAGING_MISSING",
                        "No usable staged hardware export exists. Compare hardware with TIA before confirming overwrite.");
                }

                var stagedFiles = Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories)
                    .Select(path => (Path: path, Relative: Path.GetRelativePath(stagingRoot, path)))
                    .ToArray();
                progress?.Report("Replacing saved hardware configuration from staging...");
                foreach (var staged in stagedFiles)
                {
                    var destination = WorkbenchPaths.ResolveRelative(root, staged.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(staged.Path, destination, overwrite: true);
                }

                RemoveLegacyHardwareLayout(root);
                var paths = stagedFiles
                    .Select(file => Path.Combine("hardware", file.Relative).Replace('\\', '/'))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                await versionControl.CallAsync<object>(
                    "vc_add",
                    new { repoPath = device.WorktreeRoot, paths },
                    cancellationToken).ConfigureAwait(false);
                var commit = await versionControl.CallAsync<CoordinatorGitCommitResult>(
                    "vc_commit",
                    new { repoPath = device.WorktreeRoot, message = "hardware: accept TIA configuration" },
                    cancellationToken).ConfigureAwait(false);

                return new HardwareConfigurationOverwriteResult(root, stagedFiles.Length, commit.Sha);
            },
            token);

    public ReconciliationPreview PreviewRefresh(DeviceContext device) =>
        reconciler.Preview(device);

    private async Task EnsureActiveProjectMatchesWorktreeAsync(
        DeviceContext device,
        CancellationToken cancellationToken,
        IOperationProgress? progress)
    {
        var worktree = store.Read<WorktreeMetadata>(
            Path.Combine(device.WorktreeRoot, "worktree.json"));
        if (string.IsNullOrWhiteSpace(worktree.SourceProjectPath))
        {
            throw new WorkbenchLifecycleException(
                "ENGINEERING_PROJECT_PATH_MISSING",
                $"No TIA project path is registered for worktree '{device.WorktreeId}'.");
        }

        var activeProject = await ReadActiveProjectAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(activeProject.Path)
            || !ProjectPathsEqual(worktree.SourceProjectPath, activeProject.Path))
        {
            progress?.Report("Opening the selected TIA project before continuing...");
            await engineering.CallAsync<object>(
                "disconnect",
                new { },
                cancellationToken).ConfigureAwait(false);
            var sessions = await engineering.CallAsync<SessionInfo[]>(
                "list_sessions",
                new { },
                cancellationToken).ConfigureAwait(false);
            var matchingSession = sessions.FirstOrDefault(session =>
                !string.IsNullOrWhiteSpace(session.ProjectPath)
                && ProjectPathsEqual(worktree.SourceProjectPath, session.ProjectPath));
            var connectTarget = matchingSession is not null
                ? (object)new { sessionId = matchingSession.Id }
                : new { projectPath = worktree.SourceProjectPath, withUI = true };
            await engineering.CallAsync<object>(
                "connect",
                connectTarget,
                cancellationToken).ConfigureAwait(false);
            activeProject = await ReadActiveProjectAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(activeProject.Path))
        {
            throw new WorkbenchLifecycleException(
                "ENGINEERING_PROJECT_NOT_ACTIVE",
                "TIA did not report an active project after opening the selected project.");
        }

        if (!ProjectPathsEqual(worktree.SourceProjectPath, activeProject.Path))
        {
            throw new WorkbenchLifecycleException(
                "ENGINEERING_PROJECT_MISMATCH",
                $"TIA did not switch to the selected project '{worktree.SourceProjectPath}'. "
                + $"The active project is still '{activeProject.Path}'.");
        }
    }

    private Task<ProjectInfo> ReadActiveProjectAsync(CancellationToken cancellationToken) =>
        engineering.CallAsync<ProjectInfo>("get_project_info", new { }, cancellationToken);

    private static bool ProjectPathsEqual(string left, string right)
    {
        var normalizedLeft = Path.GetFullPath(left.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRight = Path.GetFullPath(right.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> EnsureHardwareExportSucceeded(
        HardwareExportResult[] results,
        string outputRoot)
    {
        var failures = results.Where(result => !result.Success).ToArray();
        var projectAmlPath = ResolveHardwareArtifactPath(outputRoot, "project.aml");
        var projectAmlUsable = IsUsableProjectAml(projectAmlPath);
        if (!projectAmlUsable)
        {
            throw new WorkbenchLifecycleException(
                "HARDWARE_EXPORT_INCOMPLETE",
                "Hardware configuration export failed: "
                + string.Join("; ", failures.Select(result =>
                    $"{result.Scope}{(result.DeviceName is null ? string.Empty : $" '{result.DeviceName}'")}: {result.Error}")));
        }

        if (!results.Any(result => result.Scope == "project"))
        {
            throw new WorkbenchLifecycleException(
                "HARDWARE_EXPORT_INCOMPLETE",
                "Hardware configuration export did not produce a project-level AML artifact.");
        }

        return failures.Select(result =>
            $"{result.Scope}{(result.DeviceName is null ? string.Empty : $" '{result.DeviceName}'")}: {result.Error}")
            .ToArray();
    }

    private static bool IsUsableProjectAml(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return false;
        }

        try
        {
            XDocument.Load(path, LoadOptions.PreserveWhitespace);
            return true;
        }
        catch (Exception exception) when (
            exception is XmlException
            or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ResolveHardwareArtifactPath(string hardwareRoot, string fileName)
    {
        var canonicalPath = Path.Combine(hardwareRoot, fileName);
        if (File.Exists(canonicalPath))
        {
            return canonicalPath;
        }

        var legacyPath = Path.Combine(hardwareRoot, "Hardware", fileName);
        return File.Exists(legacyPath) ? legacyPath : canonicalPath;
    }

    private static bool IsUnderHardwareStaging(string hardwareRoot, string path)
    {
        var relative = Path.GetRelativePath(hardwareRoot, path);
        return relative.Equals("staging", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(
                "staging" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(
                "staging" + Path.AltDirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveLegacyHardwareLayout(string hardwareRoot)
    {
        var legacyRoot = Path.Combine(hardwareRoot, "Hardware");
        if (Directory.Exists(legacyRoot))
        {
            Directory.Delete(legacyRoot, recursive: true);
        }
    }

    private static HardwareSnapshot? ReadHardwareSnapshot(string root)
    {
        var manifestPath = ResolveHardwareArtifactPath(root, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var json = document.RootElement;
            var artifacts = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["project"] = OptionalString(json, "projectContentHash"),
            };
            if (json.TryGetProperty("devices", out var devices)
                && devices.ValueKind == JsonValueKind.Array)
            {
                foreach (var device in devices.EnumerateArray())
                {
                    var name = OptionalString(device, "deviceName");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        artifacts[HardwareSnapshot.DeviceKey(name)] =
                            OptionalString(device, "contentHash");
                    }
                }
            }

            return new HardwareSnapshot(artifacts);
        }
        catch (Exception exception) when (
            exception is JsonException
            or IOException
            or UnauthorizedAccessException)
        {
            throw new WorkbenchLifecycleException(
                "HARDWARE_MANIFEST_INVALID",
                $"The saved hardware manifest could not be read: {exception.Message}");
        }
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A failed comparison must not mask the primary result.
        }
    }

    private sealed record HardwareSnapshot(
        IReadOnlyDictionary<string, string?> Artifacts)
    {
        public static HardwareSnapshot FromResults(
            IEnumerable<HardwareExportResult> results,
            string? exportRoot = null)
        {
            var exported = results.ToArray();
            var artifacts = exported
                .Where(result => result.Success)
                .ToDictionary(
                    result => result.Scope == "project"
                        ? "project"
                        : DeviceKey(result.DeviceName ?? "(unnamed device)"),
                    result => result.ContentHash,
                    StringComparer.Ordinal);
            if (exportRoot is not null
                && exported.Any(result => result.Scope == "project")
                && !artifacts.ContainsKey("project"))
            {
                var projectAmlPath = ResolveHardwareArtifactPath(exportRoot, "project.aml");
                if (IsUsableProjectAml(projectAmlPath))
                {
                    artifacts["project"] = HashFile(projectAmlPath);
                }
            }

            return new HardwareSnapshot(artifacts);
        }

        public static IReadOnlyList<HardwareConfigurationCompareArtifact> Compare(
            HardwareSnapshot? local,
            HardwareSnapshot live)
        {
            var localArtifacts = local?.Artifacts
                ?? new Dictionary<string, string?>(StringComparer.Ordinal);
            var keys = localArtifacts.Keys
                .Concat(live.Artifacts.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();
            return keys.Select(key =>
            {
                var localExists = localArtifacts.ContainsKey(key);
                var liveExists = live.Artifacts.ContainsKey(key);
                var state = !localExists
                    ? "new"
                    : !liveExists
                        ? "missing"
                        : localArtifacts[key] is null || live.Artifacts[key] is null
                            ? "unknown"
                            : string.Equals(localArtifacts[key], live.Artifacts[key], StringComparison.Ordinal)
                                ? "same"
                                : "changed";
                var isDevice = key.StartsWith("device:", StringComparison.Ordinal);
                return new HardwareConfigurationCompareArtifact(
                    isDevice ? "device" : "project",
                    isDevice ? key["device:".Length..] : null,
                    state);
            }).ToArray();
        }

        public static string DeviceKey(string name) => "device:" + name;
    }

    public Task<RefreshApplyResult> ApplyRefreshAsync(
        DeviceContext device,
        ApprovedReconciliation approval,
        CancellationToken token,
        IOperationProgress? progress = null,
        string? commitMessage = null) =>
        operationLock.RunAsync(
            device,
            async cancellationToken =>
            {
                ArgumentNullException.ThrowIfNull(approval);
                if (!approval.Approved)
                {
                    progress?.Report("Refresh rejected by user.");
                    return new RefreshApplyResult(
                        RefreshApplyState.Rejected,
                        Array.Empty<string>(),
                        null,
                        null);
                }

                progress?.Report("Applying approved refresh...");
                var outcome = reconciler.Apply(
                    device,
                    approval.Preview,
                    approval.ApprovedPaths);
                if (outcome.ChangedPaths.Count == 0)
                {
                    progress?.Report("PLC source is already current.");
                    return new RefreshApplyResult(
                        RefreshApplyState.Committed,
                        Array.Empty<string>(),
                        null,
                        null);
                }

                var deviceMetadata = ReadDevice(device);
                WriteDevice(
                    device,
                    deviceMetadata with
                    {
                        LastExportUtc = DateTimeOffset.UtcNow.ToString("O"),
                        Knowledge = deviceMetadata.Knowledge with
                        {
                            Stale = true,
                            BaselineStale = true,
                        },
                    });
                var metadataGitPath = Path.GetRelativePath(
                        device.WorktreeRoot,
                        Path.Combine(device.DeviceRoot, "device.json"))
                    .Replace('\\', '/');
                var changedPaths = outcome.ChangedPaths
                    .Append(metadataGitPath)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                try
                {
                    progress?.Report("Staging refreshed files in Git...");
                    await versionControl.CallAsync<object>(
                        "vc_add",
                        new { repoPath = device.WorktreeRoot, paths = changedPaths },
                        cancellationToken).ConfigureAwait(false);
                    progress?.Report("Committing refreshed source...");
                    var commit = await versionControl.CallAsync<CoordinatorGitCommitResult>(
                        "vc_commit",
                        new
                        {
                            repoPath = device.WorktreeRoot,
                            message = commitMessage ?? BuildRefreshCommitMessage(device, outcome),
                        },
                        cancellationToken).ConfigureAwait(false);
                    var metadata = ReadDevice(device) with
                    {
                        LastReconciliationCommit = commit.Sha,
                    };
                    WriteDevice(device, metadata);
                    return new RefreshApplyResult(
                        RefreshApplyState.Committed,
                        changedPaths,
                        commit.Sha,
                        null);
                }
                catch (ToolCallException exception)
                {
                    return new RefreshApplyResult(
                        RefreshApplyState.FilesUpdatedCommitFailed,
                        changedPaths,
                        null,
                        $"{exception.Code}: {exception.Message}");
                }
            },
            token);

    public Task<KnowledgeUpdateResult> UpdateKnowledgeAsync(
        DeviceContext device,
        CancellationToken token,
        IOperationProgress? progress = null) =>
        operationLock.RunAsync(
            device,
            async cancellationToken =>
            {
                progress?.Report("Checking modified source overlays...");
                var relativePaths = sourceResolver.EnumerateModified(device).ToArray();
                var before = ReadDevice(device);
                KnowledgeUpdateResult result;
                if (!File.Exists(device.KnowledgeDbPath) || before.Knowledge.BaselineStale)
                {
                    progress?.Report("Ingesting device source into knowledge...");
                    var ingest = await knowledge.CallAsync<IngestResult>(
                        "ingest_source",
                        new
                        {
                            exportedSourceRoot = device.ExportedSourceRoot,
                            modifiedSourceRoot = device.ModifiedSourceRoot,
                            dbPath = device.KnowledgeDbPath,
                        },
                        cancellationToken).ConfigureAwait(false);
                    result = new KnowledgeUpdateResult(
                        ingest.DbPath,
                        relativePaths,
                        HashOverlays(device, relativePaths),
                        Array.Empty<string>());
                }
                else
                {
                    var stalePaths = relativePaths.Where(path =>
                    {
                        var hash = HashFile(WorkbenchPaths.ResolveRelative(
                            device.ModifiedSourceRoot,
                            path));
                        return !before.Knowledge.AppliedOverlayHashes.TryGetValue(path, out var applied)
                            || !string.Equals(hash, applied, StringComparison.Ordinal);
                    }).ToArray();
                    if (stalePaths.Length == 0)
                    {
                        progress?.Report("Device knowledge is already current.");
                        result = new KnowledgeUpdateResult(
                            device.KnowledgeDbPath,
                            Array.Empty<string>(),
                            before.Knowledge.AppliedOverlayHashes,
                            Array.Empty<string>());
                    }
                    else
                    {
                        progress?.Report("Updating changed knowledge components...");
                        result = await knowledge.CallAsync<KnowledgeUpdateResult>(
                            "update_components",
                            new
                            {
                                exportedSourceRoot = device.ExportedSourceRoot,
                                modifiedSourceRoot = device.ModifiedSourceRoot,
                                dbPath = device.KnowledgeDbPath,
                                relativePaths = stalePaths,
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                var appliedHashes = new Dictionary<string, string>(
                    before.Knowledge.AppliedOverlayHashes,
                    StringComparer.Ordinal);
                foreach (var applied in result.AppliedHashes)
                {
                    appliedHashes[applied.Key] = applied.Value;
                }

                var metadata = ReadDevice(device) with
                {
                    Knowledge = new KnowledgeState(
                        false,
                        appliedHashes,
                        DateTimeOffset.UtcNow.ToString("O"),
                        BaselineStale: false),
                };
                WriteDevice(device, metadata);
                return result;
            },
            token);

    public Task<KnowledgeUpdateResult> RebuildKnowledgeAsync(
        DeviceContext device,
        CancellationToken token,
        IOperationProgress? progress = null) =>
        operationLock.RunAsync(device, async cancellationToken =>
        {
            progress?.Report("Rebuilding full device knowledge...");
            var relativePaths = sourceResolver.EnumerateModified(device).ToArray();
            var ingest = await knowledge.CallAsync<IngestResult>(
                "ingest_source",
                new
                {
                    exportedSourceRoot = device.ExportedSourceRoot,
                    modifiedSourceRoot = device.ModifiedSourceRoot,
                    dbPath = device.KnowledgeDbPath,
                },
                cancellationToken).ConfigureAwait(false);
            var hashes = HashOverlays(device, relativePaths);
            var metadata = ReadDevice(device);
            WriteDevice(device, metadata with
            {
                Knowledge = new KnowledgeState(
                    false, hashes, DateTimeOffset.UtcNow.ToString("O"), BaselineStale: false),
            });
            return new KnowledgeUpdateResult(
                ingest.DbPath, relativePaths, hashes, Array.Empty<string>());
        }, token);

    /// <summary>
    /// Bootstraps a device without any user confirmation: full export staging, application of
    /// every staged file as a baseline commit, then a full knowledge ingest. Serves both the
    /// brand-new "generate PLC context" flow and the on-demand full rebuild of an established
    /// device (<paramref name="commitMessage"/> distinguishes the two in Git history).
    /// A PLC_COMPILE_REQUIRED stage failure is surfaced as-is; the bootstrap only compiles when
    /// the user explicitly acknowledged it (<paramref name="allowCompile"/>, same contract as
    /// the compare-with-TIA stage flow).
    /// </summary>
    public async Task<DeviceBootstrapResult> BootstrapDeviceAsync(
        DeviceContext device,
        CancellationToken token,
        IOperationProgress? progress = null,
        string commitMessage = "initial baseline: full export",
        bool allowCompile = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        await StageRefreshAsync(device, token, progress, allowCompile).ConfigureAwait(false);
        var preview = reconciler.Preview(device);
        var approved = preview.Entries
            .Where(entry => entry.Kind is ReconciliationChangeKind.Added or ReconciliationChangeKind.Changed)
            .Select(entry => entry.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        var baseline = await ApplyRefreshAsync(
                device,
                new ApprovedReconciliation(preview, approved),
                token,
                progress,
                commitMessage)
            .ConfigureAwait(false);
        if (baseline.State == RefreshApplyState.FilesUpdatedCommitFailed)
        {
            throw new WorkbenchLifecycleException(
                "BOOTSTRAP_COMMIT_FAILED",
                $"The baseline files were updated but the initial commit failed: {baseline.Error}");
        }

        var knowledge = await RebuildKnowledgeAsync(device, token, progress).ConfigureAwait(false);
        return new DeviceBootstrapResult(baseline, knowledge);
    }

    /// <summary>
    /// Permanently deletes a registered workbench: removes every linked worktree from the
    /// shared bare repository, deletes the whole workbench root directory, and drops the
    /// in-memory registrations. The catalog re-validates the persisted root before any
    /// directory is removed.
    /// </summary>
    public async Task DeleteWorkbenchAsync(
        WorkbenchMetadata workbench,
        CancellationToken token = default,
        IOperationProgress? progress = null)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        var persisted = catalog.Load(workbench.RootPath);
        if (!string.Equals(persisted.WorkbenchId, workbench.WorkbenchId, StringComparison.Ordinal))
        {
            throw new WorkbenchCatalogException(
                "WORKBENCH_RELATIONSHIP_MISMATCH",
                "Workbench metadata does not match the persisted catalog entry.");
        }

        foreach (var registration in persisted.Worktrees)
        {
            var worktreeRoot = WorkbenchPaths.ResolveWorktree(
                persisted.RootPath,
                registration.RelativePath);
            progress?.Report($"Removing linked worktree '{registration.Name}'...");
            try
            {
                await versionControl.CallAsync<object>(
                    "vc_remove_worktree",
                    new { repositoryPath = persisted.RepositoryPath, worktreePath = worktreeRoot },
                    token).ConfigureAwait(false);
            }
            catch (ToolCallException exception) when (
                !Directory.Exists(worktreeRoot)
                || IsMissingWorktreeRegistration(exception))
            {
                // A checkout that is already gone, or whose Git registration is stale,
                // cannot be removed by git; the wholesale directory delete below
                // finishes the project cleanup.
            }
        }

        progress?.Report("Deleting workbench directory...");
        catalog.Delete(persisted);
        knownWorkbenches.TryRemove(persisted.WorkbenchId, out _);
        foreach (var registration in persisted.Worktrees)
        {
            knownWorktrees.TryRemove(registration.WorktreeId, out _);
        }
    }

    private static bool IsMissingWorktreeRegistration(ToolCallException exception)
    {
        return string.Equals(exception.Code, "WORKTREE_REMOVE_FAILED", StringComparison.Ordinal)
            && exception.Message.Contains("is not a working tree", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ImportModifiedResult> ImportModifiedAsync(
        DeviceContext device,
        string relativePath,
        CancellationToken token,
        IOperationProgress? progress = null)
    {
        await engineeringSession.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await operationLock.RunAsync(
            device,
            async cancellationToken =>
            {
                progress?.Report("Preparing modified source import...");
                var metadata = ReadDevice(device);
                var normalized = relativePath.Replace('\\', '/');
                var modifiedPath = WorkbenchPaths.ResolveRelative(
                    device.ModifiedSourceRoot,
                    normalized);
                if (!File.Exists(modifiedPath))
                {
                    throw new FileNotFoundException(
                        "Only a modified-source overlay can be imported.",
                        modifiedPath);
                }

                ImportResult? imported = null;
                CompileResult? compiled = null;
                Exception? failure = null;
                try
                {
                    progress?.Report($"Importing {normalized}...");
                    imported = await engineering.CallAsync<ImportResult>(
                        "import_block",
                        new { xmlFilePath = modifiedPath, plcName = metadata.PlcName },
                        cancellationToken).ConfigureAwait(false);
                    if (imported.Success)
                    {
                        progress?.Report($"Compiling {imported.BlockName}...");
                        compiled = await engineering.CallAsync<CompileResult>(
                            "compile_block",
                            new { blockName = imported.BlockName, plcName = metadata.PlcName },
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failure = exception;
                }

                var warnings = imported?.Warnings ?? Array.Empty<string>();
                var record = new DeviceImportRecord(
                    normalized,
                    (imported?.ImportedAt ?? DateTime.UtcNow).ToString("O"),
                    imported?.Success == true,
                    compiled?.State ?? "not-run",
                    warnings,
                    failure?.Message ?? imported?.Error);
                WriteDevice(
                    device,
                    metadata with
                    {
                        Imports = metadata.Imports.Append(record).ToArray(),
                    });
                return new ImportModifiedResult(
                    normalized,
                    record.ImportSucceeded,
                    record.CompileState,
                    warnings,
                    record.Error);
            },
            token).ConfigureAwait(false);
        }
        finally
        {
            engineeringSession.Release();
        }
    }

    public async Task<object> MergeWorktreeAsync(
        string workbenchId,
        string sourceWorktreeId,
        string targetWorktreeId,
        CancellationToken token = default,
        IOperationProgress? progress = null)
    {
        if (string.IsNullOrWhiteSpace(workbenchId)
            || string.IsNullOrWhiteSpace(sourceWorktreeId)
            || string.IsNullOrWhiteSpace(targetWorktreeId))
        {
            throw new ArgumentException("Workbench, source worktree, and target worktree IDs are required.");
        }

        if (!knownWorkbenches.TryGetValue(workbenchId, out var workbench)
            || !knownWorktrees.TryGetValue(sourceWorktreeId, out var source)
            || !knownWorktrees.TryGetValue(targetWorktreeId, out var target)
            || !string.Equals(source.WorkbenchId, workbenchId, StringComparison.Ordinal)
            || !string.Equals(target.WorkbenchId, workbenchId, StringComparison.Ordinal))
        {
            throw new WorkbenchCatalogException(
                "WORKTREE_NOT_FOUND",
                "The requested source and target worktrees are not registered in this coordinator.");
        }

        progress?.Report("Merging worktree into target branch...");
        var targetRegistration = workbench.Worktrees.Single(item =>
            string.Equals(item.WorktreeId, targetWorktreeId, StringComparison.Ordinal));
        var targetRoot = WorkbenchPaths.ResolveWorktree(
            workbench.RootPath,
            targetRegistration.RelativePath);
        return await versionControl.CallAsync<object>(
            "vc_merge",
            new { targetWorktreePath = targetRoot, sourceBranch = source.Branch },
            token).ConfigureAwait(false);
    }

    private DeviceMetadata ReadDevice(DeviceContext device) =>
        store.Read<DeviceMetadata>(Path.Combine(device.DeviceRoot, "device.json"));

    private void WriteDevice(DeviceContext device, DeviceMetadata metadata) =>
        store.Write(Path.Combine(device.DeviceRoot, "device.json"), metadata);

    private static string ProjectIdentity(ProjectInfo project) =>
        project.Path ?? project.Name ?? throw new InvalidOperationException(
            "The engineering project did not expose a stable identity.");

    private static string SafeDirectoryName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..")
        {
            throw new ArgumentException("A valid worktree name is required.", nameof(name));
        }

        return value;
    }

    private static string BuildRefreshCommitMessage(
        DeviceContext device,
        ReconciliationOutcome outcome) =>
        $"refresh device {device.DeviceId}: reconcile {outcome.ChangedPaths.Count} files";

    private static IReadOnlyDictionary<string, string> HashOverlays(
        DeviceContext device,
        IEnumerable<string> relativePaths) =>
        relativePaths.ToDictionary(
            path => path,
            path => HashFile(WorkbenchPaths.ResolveRelative(device.ModifiedSourceRoot, path)),
            StringComparer.Ordinal);

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private IReadOnlyList<DeviceMetadata> LoadInheritedDevices(
        string worktreePath,
        string? worktreeId)
    {
        var devicesRoot = Path.Combine(worktreePath, "devices");
        if (!Directory.Exists(devicesRoot))
        {
            return Array.Empty<DeviceMetadata>();
        }

        return Directory.EnumerateDirectories(devicesRoot)
            .Select(directory => Path.Combine(directory, "device.json"))
            .Where(File.Exists)
            .Select(store.Read<DeviceMetadata>)
            .Select(device => device with
            {
                DeviceId = Guid.NewGuid().ToString("N"),
                WorktreeId = worktreeId ?? device.WorktreeId,
                LastReconciliationCommit = null,
            })
            .ToArray();
    }

}
