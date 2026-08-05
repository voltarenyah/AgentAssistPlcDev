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
    /// <summary>The user rejected the staged comparison; no source files were changed.</summary>
    Rejected = 0,

    /// <summary>The approved comparison produced no filesystem changes and no manual commit is pending.</summary>
    NoChanges = 1,

    /// <summary>Approved files were written; master TIA approvals may also include an automatic commit.</summary>
    FilesUpdated = 2,
}

/// <summary>
/// Result of applying an approved TIA comparison. CommitSha is populated when a confirmed master
/// comparison was automatically committed.
/// </summary>
public sealed record RefreshApplyResult(
    RefreshApplyState State,
    IReadOnlyList<string> ChangedPaths,
    string? CommitSha,
    string? Error);

public sealed record DeviceBootstrapResult(
    RefreshApplyResult Baseline,
    KnowledgeUpdateResult Knowledge);

public sealed record WorktreeBootstrapResult(
    IReadOnlyList<DeviceBootstrapResult> Devices);

public sealed record ImportModifiedResult(
    string RelativePath,
    bool ImportSucceeded,
    string CompileState,
    IReadOnlyList<string> Warnings,
    string? Error);

public sealed record UnauthorizedMasterRecoveryResult(
    string WorktreeId,
    string Branch,
    IReadOnlyList<string> Paths,
    string RecoveryRoot);

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
    private readonly WorkbenchConsistencyService consistency;
    private readonly FeatureImportService featureImport;
    private readonly ValidatedMergeCoordinator validatedMerge;
    private readonly RollbackFeatureService rollbackFeature;
    private readonly WorkbenchWritePolicy writePolicy;
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
        consistency = new WorkbenchConsistencyService(engineering, versionControl, catalog, store);
        featureImport = new FeatureImportService(engineering, versionControl, consistency, store);
        validatedMerge = new ValidatedMergeCoordinator(engineering, versionControl, store);
        rollbackFeature = new RollbackFeatureService(versionControl);
        writePolicy = new WorkbenchWritePolicy(store);
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
                Directory.CreateDirectory(context.SourceRoot);
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
        var persistedWorkbench = catalog.Load(request.Workbench.RootPath);
        if (!string.Equals(
                persistedWorkbench.WorkbenchId,
                request.Workbench.WorkbenchId,
                StringComparison.Ordinal))
        {
            throw new WorkbenchCatalogException(
                "WORKBENCH_RELATIONSHIP_MISMATCH",
                "Workbench metadata does not match the persisted catalog entry.");
        }

        var masterRegistrations = persistedWorkbench.Worktrees
            .Where(registration => string.Equals(
                registration.Branch,
                "master",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (masterRegistrations.Length != 1)
        {
            throw new WorkbenchCatalogException(
                "MASTER_WORKTREE_NOT_FOUND",
                $"Workbench '{persistedWorkbench.WorkbenchId}' must contain exactly one master worktree.");
        }

        var masterRegistration = masterRegistrations[0];
        var masterPath = WorkbenchPaths.ResolveWorktree(
            persistedWorkbench.RootPath,
            masterRegistration.RelativePath);
        var masterWorktree = store.Read<WorktreeMetadata>(
            Path.Combine(masterPath, "worktree.json"));
        if (!string.Equals(
                masterWorktree.WorkbenchId,
                persistedWorkbench.WorkbenchId,
                StringComparison.Ordinal)
            || !string.Equals(
                masterWorktree.WorktreeId,
                masterRegistration.WorktreeId,
                StringComparison.Ordinal))
        {
            throw new WorkbenchCatalogException(
                "WORKBENCH_RELATIONSHIP_MISMATCH",
                "Master worktree metadata does not match its catalog registration.");
        }

        // Runtime metadata is intentionally ignored by Git, so it must be captured from
        // master before the feature checkout is created. It will not appear in that checkout.
        var inheritedDevices = LoadInheritedDevices(masterPath, masterWorktree);
        var relativePath = SafeDirectoryName(request.Name);
        var worktreePath = WorkbenchPaths.ResolveWorktree(
            persistedWorkbench.RootPath,
            relativePath);
        progress?.Report("Creating linked worktree...");
        try
        {
            await versionControl.CallAsync<object>(
                "vc_add_worktree",
                new
                {
                    repositoryPath = persistedWorkbench.RepositoryPath,
                    worktreePath,
                    branchName = request.Branch,
                    startPoint = request.StartPoint,
                },
                cancellationToken).ConfigureAwait(false);

            progress?.Report("Writing worktree metadata...");
            var worktree = new WorktreeMetadata(
                WorkbenchSchema.CurrentVersion,
                Guid.NewGuid().ToString("N"),
                persistedWorkbench.WorkbenchId,
                request.Name,
                request.Branch,
                DateTimeOffset.UtcNow.ToString("O"),
                request.StartPoint,
                masterWorktree.EngineeringProjectId,
                masterWorktree.SourceProjectPath,
                inheritedDevices.Select(device => device.DeviceId).ToArray(),
                null);
            store.Write(Path.Combine(worktreePath, "worktree.json"), worktree);
            foreach (var inherited in inheritedDevices)
            {
                var updated = inherited with { WorktreeId = worktree.WorktreeId };
                var context = WorkbenchPaths.ResolveDevice(
                    persistedWorkbench.WorkbenchId,
                    persistedWorkbench.RootPath,
                    worktree.WorktreeId,
                    relativePath,
                    updated.DeviceId,
                    updated.PlcName);
                Directory.CreateDirectory(context.SourceRoot);
                Directory.CreateDirectory(context.StagingRoot);
                WriteDevice(context, updated);
            }

            var updatedWorkbench = catalog.RegisterWorktree(
                persistedWorkbench,
                new WorkbenchWorktreeRegistration(
                    worktree.WorktreeId,
                    request.Name,
                    request.Branch,
                    relativePath));
            knownWorkbenches[updatedWorkbench.WorkbenchId] = updatedWorkbench;
            knownWorktrees[worktree.WorktreeId] = worktree;
            return worktree;
        }
        catch (Exception createException)
        {
            try
            {
                await versionControl.CallAsync<object>(
                    "vc_remove_worktree",
                    new
                    {
                        repositoryPath = persistedWorkbench.RepositoryPath,
                        worktreePath,
                        branchName = request.Branch,
                        deleteBranch = true,
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Feature worktree creation failed and its linked checkout could not be removed.",
                    createException,
                    rollbackException);
            }

            throw;
        }
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
                        RefreshApplyState.NoChanges,
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
                var changedPaths = outcome.ChangedPaths
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                var sourcePaths = changedPaths
                    .Where(IsManagedSourceXml)
                    .ToArray();
                string? commitSha = null;
                var worktree = store.Read<WorktreeMetadata>(Path.Combine(device.WorktreeRoot, "worktree.json"));
                if (sourcePaths.Length > 0
                    && string.Equals(worktree.Branch, "master", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(commitMessage))
                {
                    progress?.Report("Committing the confirmed TIA source changes...");
                    var commit = await CommitSelectedSourceAsync(
                            device.WorktreeRoot,
                            sourcePaths,
                            commitMessage.Trim(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    commitSha = commit.Sha;
                }
                else
                {
                    progress?.Report("PLC source updated; select files to commit in version control.");
                }

                return new RefreshApplyResult(
                    RefreshApplyState.FilesUpdated,
                    changedPaths,
                    commitSha,
                    null);
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
                progress?.Report("Checking PLC source changes...");
                var relativePaths = sourceResolver.EnumerateSource(device).ToArray();
                var before = ReadDevice(device);
                KnowledgeUpdateResult result;
                IReadOnlyDictionary<string, string> hashesToPersist;
                if (!File.Exists(device.KnowledgeDbPath) || before.Knowledge.BaselineStale)
                {
                    progress?.Report("Ingesting device source into knowledge...");
                    var ingest = await knowledge.CallAsync<IngestResult>(
                        "ingest_source",
                        new
                        {
                            sourceRoot = device.SourceRoot,
                            dbPath = device.KnowledgeDbPath,
                        },
                        cancellationToken).ConfigureAwait(false);
                    result = new KnowledgeUpdateResult(
                        ingest.DbPath,
                        relativePaths,
                        HashOverlays(device, relativePaths),
                        Array.Empty<string>());
                    hashesToPersist = result.AppliedHashes;
                }
                else
                {
                    var stalePaths = relativePaths.Where(path =>
                    {
                        var hash = HashFile(WorkbenchPaths.ResolveRelative(
                            device.SourceRoot,
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
                        hashesToPersist = new Dictionary<string, string>(StringComparer.Ordinal);
                    }
                    else
                    {
                        progress?.Report("Updating changed knowledge components...");
                        result = await knowledge.CallAsync<KnowledgeUpdateResult>(
                            "update_components",
                            new
                            {
                                sourceRoot = device.SourceRoot,
                                dbPath = device.KnowledgeDbPath,
                                relativePaths = stalePaths,
                            },
                            cancellationToken).ConfigureAwait(false);
                        hashesToPersist = HashOverlays(device, stalePaths);
                    }
                }
                var appliedHashes = new Dictionary<string, string>(
                    before.Knowledge.AppliedOverlayHashes,
                    StringComparer.Ordinal);
                foreach (var applied in hashesToPersist)
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
            var relativePaths = sourceResolver.EnumerateSource(device).ToArray();
            var ingest = await knowledge.CallAsync<IngestResult>(
                "ingest_source",
                new
                {
                    sourceRoot = device.SourceRoot,
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
    /// every staged file to the source tree, then a full knowledge ingest. The first successful
    /// rebuild creates the initial source baseline commit; later rebuilds remain pending so the
    /// user can select and commit them manually.
    /// A PLC_COMPILE_REQUIRED stage failure is surfaced as-is; the bootstrap only compiles when
    /// the user explicitly acknowledged it (<paramref name="allowCompile"/>, same contract as
    /// the compare-with-TIA stage flow).
    /// </summary>
    public async Task<DeviceBootstrapResult> BootstrapDeviceAsync(
        DeviceContext device,
        CancellationToken token,
        IOperationProgress? progress = null,
        bool allowCompile = false,
        string? commitMessage = null)
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
                progress)
            .ConfigureAwait(false);

        var initialSourcePaths = baseline.ChangedPaths
            .Where(IsManagedSourceXml)
            .ToArray();
        if (initialSourcePaths.Length > 0
            && !await HasVersionControlHistoryAsync(device.WorktreeRoot, token).ConfigureAwait(false))
        {
            progress?.Report("Creating the initial PLC source baseline commit...");
            var commit = await CommitSelectedSourceAsync(
                    device.WorktreeRoot,
                    initialSourcePaths,
                    string.IsNullOrWhiteSpace(commitMessage) ? "Initial PLC source baseline" : commitMessage.Trim(),
                    token)
                .ConfigureAwait(false);
            baseline = baseline with { CommitSha = commit.Sha };
        }

        var knowledge = await RebuildKnowledgeAsync(device, token, progress).ConfigureAwait(false);
        return new DeviceBootstrapResult(baseline, knowledge);
    }

    /// <summary>
    /// Bootstraps every PLC registered in one worktree as one project-level operation. Staging
    /// is completed for all devices before any source tree is changed, then the first successful
    /// rebuild creates one baseline commit containing every device's managed XML source.
    /// </summary>
    public async Task<WorktreeBootstrapResult> BootstrapWorktreeAsync(
        DeviceContext selectedDevice,
        CancellationToken token,
        IOperationProgress? progress = null,
        bool allowCompile = false,
        string? commitMessage = null)
    {
        ArgumentNullException.ThrowIfNull(selectedDevice);
        var devices = LoadWorktreeContexts(selectedDevice);
        if (devices.Count == 0)
        {
            throw new WorkbenchCatalogException(
                "DEVICE_NOT_FOUND",
                $"Worktree '{selectedDevice.WorktreeId}' has no registered PLC devices.");
        }

        progress?.Report($"Exporting {devices.Count} PLC device(s) from TIA...");
        var staged = new List<(DeviceContext Context, ReconciliationPreview Preview)>(devices.Count);
        foreach (var device in devices)
        {
            await StageRefreshAsync(device, token, progress, allowCompile).ConfigureAwait(false);
            staged.Add((device, reconciler.Preview(device)));
        }

        var baselines = new List<DeviceBootstrapResult>(devices.Count);
        foreach (var (device, preview) in staged)
        {
            var approved = preview.Entries
                .Where(entry => entry.Kind is ReconciliationChangeKind.Added or ReconciliationChangeKind.Changed)
                .Select(entry => entry.RelativePath)
                .ToHashSet(StringComparer.Ordinal);
            var baseline = await ApplyRefreshAsync(
                    device,
                    new ApprovedReconciliation(preview, approved),
                    token,
                    progress)
                .ConfigureAwait(false);
            baselines.Add(new DeviceBootstrapResult(
                baseline,
                new KnowledgeUpdateResult(
                    device.KnowledgeDbPath,
                    Array.Empty<string>(),
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    Array.Empty<string>())));
        }

        var initialSourcePaths = baselines
            .SelectMany(result => result.Baseline.ChangedPaths)
            .Where(IsManagedSourceXml)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        string? commitSha = null;
        if (initialSourcePaths.Length > 0
            && !await HasVersionControlHistoryAsync(selectedDevice.WorktreeRoot, token).ConfigureAwait(false))
        {
            progress?.Report("Creating the initial PLC source baseline commit for all devices...");
            var commit = await CommitSelectedSourceAsync(
                    selectedDevice.WorktreeRoot,
                    initialSourcePaths,
                    string.IsNullOrWhiteSpace(commitMessage) ? "Initial PLC source baseline" : commitMessage.Trim(),
                    token)
                .ConfigureAwait(false);
            commitSha = commit.Sha;
            baselines = baselines
                .Select(result => result with { Baseline = result.Baseline with { CommitSha = commitSha } })
                .ToList();
        }

        for (var index = 0; index < baselines.Count; index++)
        {
            var knowledge = await RebuildKnowledgeAsync(
                    devices[index],
                    token,
                    progress)
                .ConfigureAwait(false);
            baselines[index] = baselines[index] with { Knowledge = knowledge };
        }

        return new WorktreeBootstrapResult(baselines);
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
                progress?.Report("Preparing PLC source import...");
                var metadata = ReadDevice(device);
                var normalized = relativePath.Replace('\\', '/');
                var modifiedPath = WorkbenchPaths.ResolveRelative(
                    device.SourceRoot,
                    normalized);
                if (!File.Exists(modifiedPath))
                {
                    throw new FileNotFoundException(
                        "Only an existing PLC source XML file can be imported.",
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

    public async Task<WorkbenchConsistencyResult> CompareMasterWithTiaAsync(
        string workbenchId,
        CancellationToken token = default,
        IOperationProgress? progress = null)
    {
        var workbench = LoadRegisteredWorkbench(workbenchId);
        var masterRegistration = workbench.Worktrees.SingleOrDefault(item =>
                string.Equals(item.Branch, "master", StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkbenchCatalogException("MASTER_WORKTREE_NOT_FOUND", "The workbench has no master worktree.");
        var masterRoot = WorkbenchPaths.ResolveWorktree(workbench.RootPath, masterRegistration.RelativePath);
        var master = store.Read<WorktreeMetadata>(Path.Combine(masterRoot, "worktree.json"));
        return await consistency.CompareAsync(workbench, master, token, progress).ConfigureAwait(false);
    }

    public WorkbenchConsistencyResult GetComparison(string workbenchId, string comparisonId)
    {
        var workbench = LoadRegisteredWorkbench(workbenchId);
        return consistency.GetComparison(workbench, comparisonId);
    }

    public async Task<FeatureImportPlan> PlanFeatureImportAsync(
        string workbenchId,
        string featureWorktreeId,
        CancellationToken token = default)
    {
        var workbench = LoadRegisteredWorkbench(workbenchId);
        var feature = LoadRegisteredWorktree(workbench, featureWorktreeId);
        if (string.Equals(feature.Branch, "master", StringComparison.OrdinalIgnoreCase))
            throw new WorkbenchLifecycleException("FEATURE_WORKTREE_REQUIRED", "Import plans require a feature worktree.");
        return await featureImport.PlanAsync(workbench, feature, token).ConfigureAwait(false);
    }

    public Task<FeatureImportSession> ImportFeatureAsync(
        string workbenchId,
        string planId,
        IReadOnlyList<string> paths,
        CancellationToken token = default) =>
        featureImport.ImportAsync(LoadRegisteredWorkbench(workbenchId), planId, paths, token);

    public Task<FeatureImportSession> RollbackFeatureImportAsync(
        string workbenchId,
        string sessionId,
        IReadOnlyList<string> paths,
        CancellationToken token = default) =>
        featureImport.RollbackAsync(LoadRegisteredWorkbench(workbenchId), sessionId, paths, token);

    public FeatureImportSession KeepFeatureImportAfterCompileFailure(
        string workbenchId,
        string sessionId,
        IReadOnlyList<string> paths) =>
        featureImport.KeepAfterCompileFailure(LoadRegisteredWorkbench(workbenchId), sessionId, paths);

    public FeatureImportPlan GetFeatureImportPlan(string workbenchId, string planId) =>
        featureImport.ReadPlan(LoadRegisteredWorkbench(workbenchId), planId);

    public FeatureImportSession GetFeatureImportSession(string workbenchId, string sessionId) =>
        featureImport.ReadSession(LoadRegisteredWorkbench(workbenchId), sessionId);

    public async Task<ValidatedMergeResult> ValidateFeatureMergeAsync(
        ValidateFeatureMergeRequest request,
        CancellationToken token = default,
        IOperationProgress? progress = null)
    {
        var workbench = LoadRegisteredWorkbench(request.WorkbenchId);
        var feature = LoadRegisteredWorktree(workbench, request.FeatureWorktreeId);
        var session = featureImport.ReadSession(workbench, request.ImportSessionId);
        return await validatedMerge.ValidateAsync(workbench, feature, session, request, token, progress).ConfigureAwait(false);
    }

    public ValidatedMergeDraft GetValidatedMerge(string workbenchId, string validationId) =>
        validatedMerge.ReadDraft(LoadRegisteredWorkbench(workbenchId), validationId);

    public async Task<RollbackFeatureResult> CreateRollbackFeatureAsync(
        string workbenchId,
        string historicalSha,
        IReadOnlyList<string> paths,
        string featureName,
        CancellationToken token = default)
    {
        var workbench = LoadRegisteredWorkbench(workbenchId);
        var master = LoadRegisteredWorktree(workbench, workbench.Worktrees.Single(item => string.Equals(item.Branch, "master", StringComparison.OrdinalIgnoreCase)).WorktreeId);
        var branch = $"rollback/{SafeDirectoryName(featureName)}-{Guid.NewGuid():N}";
        var feature = await CreateWorktreeAsync(new CreateWorktreeRequest(workbench, featureName, branch, master.BaseCommit), token).ConfigureAwait(false);
        var featureRoot = ResolveWorktreeRoot(workbench, feature.WorktreeId);
        try
        {
            var applied = await rollbackFeature.ApplyAsync(featureRoot, historicalSha, paths, token).ConfigureAwait(false);
            return applied with { WorktreeId = feature.WorktreeId, Branch = branch };
        }
        catch
        {
            await DeleteWorktreeAsync(workbench, feature.WorktreeId, token).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<FeatureMergePublicationResult> MergeValidatedAsync(
        string workbenchId,
        string validationId,
        CancellationToken token = default)
    {
        var workbench = LoadRegisteredWorkbench(workbenchId);
        var draft = validatedMerge.ReadDraft(workbench, validationId);
        var targetRoot = ResolveWorktreeRoot(workbench, workbench.Worktrees.Single(item => string.Equals(item.Branch, "master", StringComparison.OrdinalIgnoreCase)).WorktreeId);
        var evidence = new FeatureMergeEvidenceDto
        {
            SchemaVersion = "1.0",
            EvidenceKind = "feature-merge",
            CommitSha = draft.TargetSha,
            WorkbenchId = draft.WorkbenchId,
            SourceWorktreeId = draft.FeatureWorktreeId,
            ConfirmedAt = draft.ConfirmedAt,
            ConfirmedBy = draft.ConfirmedBy,
            MachineValidated = true,
            Devices = draft.Devices.Select(device => new FeatureMergeEvidenceDeviceDto
            {
                DeviceId = device.DeviceId,
                PlcName = device.PlcName,
                ProjectIdentity = device.ProjectIdentity,
                ProjectChecksum = device.ProjectChecksum,
                Objects = device.Objects.Select(item => new FeatureMergeEvidenceObjectDto { Identity = item.Identity, RelativePath = item.RelativePath, Sha256 = item.Sha256 }).ToArray(),
            }).ToArray(),
        };
        var request = new
        {
            targetWorktreePath = targetRoot,
            sourceBranch = draft.SourceBranch,
            expectedTargetSha = draft.TargetSha,
            expectedSourceSha = draft.SourceSha,
            candidateTreeSha = draft.CandidateTreeSha,
            evidence,
        };
        var result = await versionControl.CallAsync<FeatureMergePublicationResult>("vc_merge_validated", request, token).ConfigureAwait(false);
        var path = Path.Combine(workbench.RootPath, ".automation", "validated-merges", validationId + ".json");
        if (File.Exists(path)) File.Delete(path);
        return result;
    }

    public async Task<TiaSyncEvidence> ValidateSynchronizedMasterAsync(
        string workbenchId,
        string confirmedBy,
        CancellationToken token = default,
        IOperationProgress? progress = null)
    {
        var workbench = LoadRegisteredWorkbench(workbenchId);
        var masterRegistration = workbench.Worktrees.SingleOrDefault(item =>
                string.Equals(item.Branch, "master", StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkbenchCatalogException("MASTER_WORKTREE_NOT_FOUND", "The workbench has no master worktree.");
        var masterRoot = WorkbenchPaths.ResolveWorktree(workbench.RootPath, masterRegistration.RelativePath);
        var master = store.Read<WorktreeMetadata>(Path.Combine(masterRoot, "worktree.json"));
        return await consistency.ValidateSynchronizedMasterAsync(workbench, master, confirmedBy, token, progress)
            .ConfigureAwait(false);
    }

    public async Task<TiaSynchronizationResult> ApplyTiaSynchronizationAsync(
        string workbenchId,
        string comparisonId,
        IReadOnlyList<string> paths,
        string message,
        CancellationToken token = default,
        IOperationProgress? progress = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A commit title is required.", nameof(message));

        var workbench = LoadRegisteredWorkbench(workbenchId);
        var masterRegistration = workbench.Worktrees.SingleOrDefault(item =>
                string.Equals(item.Branch, "master", StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkbenchCatalogException("MASTER_WORKTREE_NOT_FOUND", "The workbench has no master worktree.");
        var masterRoot = WorkbenchPaths.ResolveWorktree(workbench.RootPath, masterRegistration.RelativePath);
        var master = store.Read<WorktreeMetadata>(Path.Combine(masterRoot, "worktree.json"));
        var comparison = consistency.GetComparison(workbench, comparisonId);
        var selected = NormalizeSourcePaths(paths);
        var contexts = LoadMasterContexts(workbench, master)
            .ToDictionary(item => item.Metadata.DeviceId, item => item.Context, StringComparer.Ordinal);
        var pending = writePolicy.ReadPending(masterRoot, master.WorktreeId).Sources.ToList();

        foreach (var path in selected)
        {
            var difference = comparison.Differences.SingleOrDefault(item => item.RelativePath == path)
                ?? throw new WorkbenchLifecycleException(
                    "SOURCE_NOT_IN_COMPARISON",
                    $"Source '{path}' is not an authorized difference in comparison '{comparisonId}'.");
            if (difference.Kind == SourceDifferenceKind.Deleted)
            {
                throw new WorkbenchLifecycleException(
                    "SOURCE_DELETE_UNSUPPORTED",
                    $"Deleting source '{path}' from TIA is not supported by the current import flow.");
            }
            if (difference.Kind is not (SourceDifferenceKind.Changed or SourceDifferenceKind.Added))
                throw new WorkbenchLifecycleException("SOURCE_NOT_IMPORTABLE", $"Source '{path}' is not importable.");
            if (!contexts.TryGetValue(difference.DeviceId, out var context))
                throw new WorkbenchCatalogException("DEVICE_NOT_FOUND", $"Device '{difference.DeviceId}' was not found in master.");

            var sourceRelativePath = ExtractSourceRelativePath(path);
            var staged = WorkbenchPaths.ResolveRelative(context.StagingRoot, sourceRelativePath);
            var destination = WorkbenchPaths.ResolveRelative(masterRoot, path);
            if (!File.Exists(staged))
                throw new WorkbenchLifecycleException("TIA_SOURCE_MISSING", $"The staged source '{sourceRelativePath}' is missing.");

            CopyFileAtomically(staged, destination);
            var copiedFingerprint = HashFile(destination);
            pending.RemoveAll(item => string.Equals(item.RelativePath, path, StringComparison.Ordinal));
            pending.Add(new PendingMasterSource(
                path,
                comparisonId,
                comparison.MasterSha,
                difference.TiaFingerprint ?? copiedFingerprint,
                copiedFingerprint));
            var metadata = store.Read<DeviceMetadata>(Path.Combine(context.DeviceRoot, "device.json"));
            store.Write(Path.Combine(context.DeviceRoot, "device.json"), metadata with
            {
                Knowledge = metadata.Knowledge with { Stale = true, BaselineStale = true },
            });
            progress?.Report($"Accepted TIA source {path} into master.");
        }

        var normalizedPending = pending
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
        writePolicy.WritePending(masterRoot, new PendingMasterSynchronization(
            WorkbenchWritePolicy.PendingSchemaVersion,
            master.WorktreeId,
            normalizedPending));
        var commit = await CommitSourceAsync(
                workbenchId,
                master.WorktreeId,
                selected,
                message.Trim(),
                token)
            .ConfigureAwait(false);
        var remaining = writePolicy.ReadPending(masterRoot, master.WorktreeId).Sources;
        return new TiaSynchronizationResult(
            comparisonId,
            remaining.Select(item => item.RelativePath).ToArray(),
            commit.Sha);
    }

    public async Task<WorkbenchCommitResult> CommitSourceAsync(
        string workbenchId,
        string worktreeId,
        IReadOnlyList<string> paths,
        string message,
        CancellationToken token = default,
        string? author = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A commit message is required.", nameof(message));
        var workbench = LoadRegisteredWorkbench(workbenchId);
        var registration = workbench.Worktrees.SingleOrDefault(item => item.WorktreeId == worktreeId)
            ?? throw new WorkbenchCatalogException("WORKTREE_NOT_FOUND", $"Worktree '{worktreeId}' was not found.");
        var worktreeRoot = WorkbenchPaths.ResolveWorktree(workbench.RootPath, registration.RelativePath);
        var worktree = store.Read<WorktreeMetadata>(Path.Combine(worktreeRoot, "worktree.json"));
        var selected = NormalizeSourcePaths(paths);

        if (string.Equals(worktree.Branch, "master", StringComparison.OrdinalIgnoreCase))
        {
            var pending = writePolicy.ReadPending(worktreeRoot, worktree.WorktreeId);
            var authorized = pending.Sources.Where(item => selected.Contains(item.RelativePath, StringComparer.Ordinal)).ToArray();
            if (authorized.Length != selected.Length)
                throw new WorkbenchLifecycleException(
                    "MASTER_CHANGE_NOT_AUTHORIZED",
                    "Every selected master source file must first be accepted from a TIA comparison.");

            foreach (var item in authorized)
            {
                var source = WorkbenchPaths.ResolveRelative(worktreeRoot, item.RelativePath);
                if (!File.Exists(source) || !string.Equals(HashFile(source), item.CopiedFileFingerprint, StringComparison.Ordinal))
                    throw new WorkbenchLifecycleException(
                        "MASTER_CHANGE_NOT_AUTHORIZED",
                        $"Master source '{item.RelativePath}' changed after TIA authorization.");
            }

            var head = await ReadMasterHeadAsync(worktreeRoot, token).ConfigureAwait(false);
            if (authorized.Any(item => !string.Equals(item.MasterHeadSha, head, StringComparison.OrdinalIgnoreCase)))
                throw new WorkbenchLifecycleException(
                    "MASTER_HEAD_CHANGED",
                    "Master advanced after TIA authorization; compare TIA with master again before committing.");
        }

        var result = await CommitSelectedSourceAsync(worktreeRoot, selected, message, token, author)
            .ConfigureAwait(false);

        if (string.Equals(worktree.Branch, "master", StringComparison.OrdinalIgnoreCase))
        {
            var remaining = writePolicy.ReadPending(worktreeRoot, worktree.WorktreeId).Sources
                .Where(item => !selected.Contains(item.RelativePath, StringComparer.Ordinal))
                .ToArray();
            var newHead = await ReadMasterHeadAsync(worktreeRoot, token).ConfigureAwait(false);
            writePolicy.WritePending(worktreeRoot, new PendingMasterSynchronization(
                WorkbenchWritePolicy.PendingSchemaVersion,
                worktree.WorktreeId,
                remaining.Select(item => item with { MasterHeadSha = newHead }).ToArray()));
        }

        return result;
    }

    private async Task<WorkbenchCommitResult> CommitSelectedSourceAsync(
        string worktreeRoot,
        IReadOnlyList<string> paths,
        string message,
        CancellationToken token,
        string? author = null)
    {
        return await versionControl.CallAsync<WorkbenchCommitResult>(
                "vc_commit_selected",
                new { repoPath = worktreeRoot, paths, message, author },
                token)
            .ConfigureAwait(false);
    }

    private async Task<bool> HasVersionControlHistoryAsync(string worktreeRoot, CancellationToken token)
    {
        var log = await versionControl.CallAsync<ConsistencyLogResult>(
                "vc_log",
                new { repoPath = worktreeRoot, maxCount = 1 },
                token)
            .ConfigureAwait(false);
        return log.Commits.Length > 0;
    }

    public async Task<UnauthorizedMasterRecoveryResult> MoveUnauthorizedMasterChangesAsync(
        string workbenchId,
        IReadOnlyList<string> paths,
        string featureName,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(featureName))
            throw new ArgumentException("A feature name is required.", nameof(featureName));
        var workbench = catalog.Load(knownWorkbenches.TryGetValue(workbenchId, out var known)
            ? known.RootPath : throw new WorkbenchCatalogException("WORKBENCH_NOT_FOUND", "The workbench is not registered."));
        RegisterWorkbench(workbench);
        var masterRegistration = workbench.Worktrees.SingleOrDefault(item =>
            string.Equals(item.Branch, "master", StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkbenchCatalogException("MASTER_WORKTREE_NOT_FOUND", "The workbench has no master worktree.");
        var masterRoot = WorkbenchPaths.ResolveWorktree(workbench.RootPath, masterRegistration.RelativePath);
        var selected = NormalizeSourcePaths(paths);
        var recoveryRoot = Path.Combine(masterRoot, ".automation", "recovery", $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{SafeDirectoryName(featureName)}");
        Directory.CreateDirectory(recoveryRoot);
        var evidence = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in selected)
        {
            var source = WorkbenchPaths.ResolveRelative(masterRoot, path);
            if (!File.Exists(source))
                throw new FileNotFoundException("The selected master source file was not found.", source);
            var recoveryPath = Path.Combine(recoveryRoot, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(recoveryPath)!);
            File.Copy(source, recoveryPath, overwrite: true);
            evidence[path] = HashFile(source);
        }
        store.Write(Path.Combine(recoveryRoot, "recovery.json"), evidence);

        var branch = $"recovery/{SafeDirectoryName(featureName)}";
        var feature = await CreateWorktreeAsync(
            new CreateWorktreeRequest(workbench, featureName, branch, null), token).ConfigureAwait(false);
        var featureRegistration = catalog.Load(workbench.RootPath).Worktrees.Single(item => item.WorktreeId == feature.WorktreeId);
        var featureRoot = WorkbenchPaths.ResolveWorktree(workbench.RootPath, featureRegistration.RelativePath);
        foreach (var path in selected)
        {
            var destination = WorkbenchPaths.ResolveRelative(featureRoot, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(WorkbenchPaths.ResolveRelative(recoveryRoot, path), destination, overwrite: true);
            if (!string.Equals(HashFile(destination), evidence[path], StringComparison.Ordinal))
                throw new WorkbenchLifecycleException("RECOVERY_VERIFY_FAILED", $"Recovered source '{path}' failed verification.");
        }

        try
        {
            foreach (var path in selected)
            {
                await versionControl.CallAsync<object>(
                    "vc_restore",
                    new { repoPath = masterRoot, filePath = path, sourceSha = (string?)null },
                    token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Keep master exactly as it was before recovery if one selected restore fails.
            foreach (var path in selected)
            {
                var original = WorkbenchPaths.ResolveRelative(recoveryRoot, path);
                var destination = WorkbenchPaths.ResolveRelative(masterRoot, path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(original, destination, overwrite: true);
            }
            throw;
        }

        return new UnauthorizedMasterRecoveryResult(feature.WorktreeId, feature.Branch, selected, recoveryRoot);
    }

    public async Task DiscardUnauthorizedMasterChangesAsync(
        string workbenchId,
        IReadOnlyList<string> paths,
        CancellationToken token = default)
    {
        var workbench = catalog.Load(knownWorkbenches.TryGetValue(workbenchId, out var known)
            ? known.RootPath : throw new WorkbenchCatalogException("WORKBENCH_NOT_FOUND", "The workbench is not registered."));
        RegisterWorkbench(workbench);
        var masterRegistration = workbench.Worktrees.SingleOrDefault(item =>
            string.Equals(item.Branch, "master", StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkbenchCatalogException("MASTER_WORKTREE_NOT_FOUND", "The workbench has no master worktree.");
        var masterRoot = WorkbenchPaths.ResolveWorktree(workbench.RootPath, masterRegistration.RelativePath);
        foreach (var path in NormalizeSourcePaths(paths))
        {
            await versionControl.CallAsync<object>(
                "vc_restore",
                new { repoPath = masterRoot, filePath = path, sourceSha = (string?)null },
                token).ConfigureAwait(false);
        }
    }

    private WorkbenchMetadata LoadRegisteredWorkbench(string workbenchId)
    {
        if (!knownWorkbenches.TryGetValue(workbenchId, out var known))
            throw new WorkbenchCatalogException("WORKBENCH_NOT_FOUND", "The workbench is not registered.");

        var workbench = catalog.Load(known.RootPath);
        RegisterWorkbench(workbench);
        return workbench;
    }

    private WorktreeMetadata LoadRegisteredWorktree(WorkbenchMetadata workbench, string worktreeId)
    {
        var registration = workbench.Worktrees.SingleOrDefault(item => item.WorktreeId == worktreeId)
            ?? throw new WorkbenchCatalogException("WORKTREE_NOT_FOUND", $"Worktree '{worktreeId}' was not found.");
        return store.Read<WorktreeMetadata>(Path.Combine(
            WorkbenchPaths.ResolveWorktree(workbench.RootPath, registration.RelativePath),
            "worktree.json"));
    }

    private static string ResolveWorktreeRoot(WorkbenchMetadata workbench, string worktreeId) =>
        WorkbenchPaths.ResolveWorktree(workbench.RootPath, workbench.Worktrees.Single(item => item.WorktreeId == worktreeId).RelativePath);

    private IReadOnlyList<(DeviceMetadata Metadata, DeviceContext Context)> LoadMasterContexts(
        WorkbenchMetadata workbench,
        WorktreeMetadata master)
    {
        var masterRoot = WorkbenchPaths.ResolveWorktree(
            workbench.RootPath,
            workbench.Worktrees.Single(item => item.WorktreeId == master.WorktreeId).RelativePath);
        return LoadInheritedDevices(masterRoot, master)
            .Select(device => (device, catalog.ResolveDevice(workbench, master, device)))
            .ToArray();
    }

    private async Task<string> ReadMasterHeadAsync(string worktreeRoot, CancellationToken token)
    {
        var log = await versionControl.CallAsync<ConsistencyLogResult>(
                "vc_log",
                new { repoPath = worktreeRoot, maxCount = 1 },
                token)
            .ConfigureAwait(false);
        return log.Commits.FirstOrDefault()?.Sha
            ?? throw new WorkbenchLifecycleException("MASTER_HEAD_UNAVAILABLE", "The master worktree has no Git HEAD.");
    }

    private static string ExtractSourceRelativePath(string repositoryPath)
    {
        var normalized = repositoryPath.Replace('\\', '/');
        var marker = "/source/";
        var start = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            throw new WorkbenchPathException($"'{repositoryPath}' is not a managed PLC source path.");
        return normalized[(start + marker.Length)..];
    }

    private static void CopyFileAtomically(string source, string destination)
    {
        var parent = Path.GetDirectoryName(destination)
            ?? throw new IOException($"The destination has no parent directory: {destination}");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string[] NormalizeSourcePaths(IEnumerable<string> paths)
    {
        var normalized = paths
            .Select(path => path.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
            throw new ArgumentException("At least one PLC source XML path is required.", nameof(paths));
        foreach (var path in normalized)
        {
            var parts = path.Split('/');
            if (parts.Length < 4 || !parts[0].Equals("devices", StringComparison.OrdinalIgnoreCase)
                || !parts[2].Equals("source", StringComparison.OrdinalIgnoreCase)
                || !parts[^1].EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                throw new WorkbenchPathException($"'{path}' is not a PLC source XML path.");
            _ = WorkbenchPaths.ResolveRelative(Path.GetTempPath(), path);
        }
        return normalized;
    }

    private static bool IsManagedSourceXml(string path)
    {
        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/');
        return parts.Length >= 4
            && parts[0].Equals("devices", StringComparison.OrdinalIgnoreCase)
            && parts[2].Equals("source", StringComparison.OrdinalIgnoreCase)
            && parts[^1].EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    }

    private DeviceMetadata ReadDevice(DeviceContext device) =>
        store.Read<DeviceMetadata>(Path.Combine(device.DeviceRoot, "device.json"));

    private IReadOnlyList<DeviceContext> LoadWorktreeContexts(DeviceContext selectedDevice)
    {
        var worktree = store.Read<WorktreeMetadata>(
            Path.Combine(selectedDevice.WorktreeRoot, "worktree.json"));
        var worktreeParent = Path.Combine(selectedDevice.WorkbenchRoot, "worktrees");
        var relativePath = Path.GetRelativePath(worktreeParent, selectedDevice.WorktreeRoot);
        return LoadInheritedDevices(selectedDevice.WorktreeRoot, worktree)
            .Select(metadata => WorkbenchPaths.ResolveDevice(
                selectedDevice.WorkbenchId,
                selectedDevice.WorkbenchRoot,
                worktree.WorktreeId,
                relativePath,
                metadata.DeviceId,
                metadata.PlcName))
            .ToArray();
    }

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

    private static IReadOnlyDictionary<string, string> HashOverlays(
        DeviceContext device,
        IEnumerable<string> relativePaths) =>
        relativePaths.ToDictionary(
            path => path,
            path => HashFile(WorkbenchPaths.ResolveRelative(device.SourceRoot, path)),
            StringComparer.Ordinal);

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private IReadOnlyList<DeviceMetadata> LoadInheritedDevices(
        string worktreePath,
        WorktreeMetadata worktree)
    {
        var devicesRoot = Path.Combine(worktreePath, "devices");
        if (!Directory.Exists(devicesRoot) && worktree.DeviceIds.Count == 0)
        {
            return Array.Empty<DeviceMetadata>();
        }

        var devicesById = Directory.EnumerateDirectories(devicesRoot)
            .OrderBy(directory => directory, StringComparer.Ordinal)
            .Select(directory => Path.Combine(directory, "device.json"))
            .Where(File.Exists)
            .Select(store.Read<DeviceMetadata>)
            .ToDictionary(device => device.DeviceId, StringComparer.Ordinal);
        var inherited = new List<DeviceMetadata>(worktree.DeviceIds.Count);
        foreach (var deviceId in worktree.DeviceIds)
        {
            if (!devicesById.TryGetValue(deviceId, out var device)
                || !string.Equals(
                    device.WorktreeId,
                    worktree.WorktreeId,
                    StringComparison.Ordinal))
            {
                throw new WorkbenchCatalogException(
                    "WORKBENCH_RELATIONSHIP_MISMATCH",
                    $"Device metadata '{deviceId}' does not match master worktree '{worktree.WorktreeId}'.");
            }

            inherited.Add(device);
        }

        if (devicesById.Count != inherited.Count)
        {
            throw new WorkbenchCatalogException(
                "WORKBENCH_RELATIONSHIP_MISMATCH",
                "Master worktree device metadata does not match its registered device IDs.");
        }

        return inherited;
    }

}
