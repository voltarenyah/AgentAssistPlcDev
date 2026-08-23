using Agent.Mcp;
using Contracts.Engineering;

namespace Agent.Workbench;

/// <summary>Plans and executes selective import of committed feature XML into TIA.</summary>
public sealed class FeatureImportService
{
    private readonly IMcpToolCaller engineering;
    private readonly IMcpToolCaller versionControl;
    private readonly WorkbenchConsistencyService consistency;
    private readonly AtomicJsonStore store;

    public FeatureImportService(
        IMcpToolCaller engineering,
        IMcpToolCaller versionControl,
        WorkbenchConsistencyService consistency,
        AtomicJsonStore? store = null)
    {
        this.engineering = engineering ?? throw new ArgumentNullException(nameof(engineering));
        this.versionControl = versionControl ?? throw new ArgumentNullException(nameof(versionControl));
        this.consistency = consistency ?? throw new ArgumentNullException(nameof(consistency));
        this.store = store ?? new AtomicJsonStore();
    }

    public async Task<FeatureImportPlan> PlanAsync(
        WorkbenchMetadata workbench,
        WorktreeMetadata feature,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        ArgumentNullException.ThrowIfNull(feature);

        var master = LoadMaster(workbench);
        var featureRoot = ResolveWorktreeRoot(workbench, feature.WorktreeId);
        var masterRoot = ResolveWorktreeRoot(workbench, master.WorktreeId);
        var status = await versionControl.CallAsync<ConsistencyStatusResult>(
            "vc_status", new { repoPath = featureRoot }, cancellationToken).ConfigureAwait(false);
        if (status.Entries.Any(item => IsManagedSource(item.FilePath)))
            throw new WorkbenchLifecycleException(
                "FEATURE_SOURCE_UNCOMMITTED",
                "Commit the feature source XML before creating an import plan.");

        var featureLog = await versionControl.CallAsync<ConsistencyLogResult>(
            "vc_log", new { repoPath = featureRoot, maxCount = 1 }, cancellationToken).ConfigureAwait(false);
        var masterLog = await versionControl.CallAsync<ConsistencyLogResult>(
            "vc_log", new { repoPath = masterRoot, maxCount = 1 }, cancellationToken).ConfigureAwait(false);
        var featureSha = featureLog.Commits.FirstOrDefault()?.Sha
            ?? throw new WorkbenchLifecycleException("FEATURE_HEAD_UNAVAILABLE", "The feature has no committed HEAD.");
        var masterSha = masterLog.Commits.FirstOrDefault()?.Sha
            ?? throw new WorkbenchLifecycleException("MASTER_HEAD_UNAVAILABLE", "The master has no committed HEAD.");

        var preview = await versionControl.CallAsync<FeaturePreviewDto>(
            "vc_merge_preview",
            new { repoPath = masterRoot, sourceBranch = feature.Branch },
            cancellationToken).ConfigureAwait(false);
        if (preview.HasConflicts)
            throw new WorkbenchLifecycleException(
                "GIT_MERGE_CONFLICT",
                $"The feature has Git conflicts: {string.Join(", ", preview.ConflictPaths)}.");

        var masterComparison = await consistency.CompareAsync(
            workbench,
            master,
            cancellationToken,
            forceFullExport: false).ConfigureAwait(false);
        var tiaPaths = masterComparison.Differences
            .Where(item => !string.IsNullOrWhiteSpace(item.RelativePath))
            .Select(item => item.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        var masterDevices = LoadDevices(workbench, master);
        var objects = preview.Objects
            .Where(item => IsManagedSource(item.FilePath))
            .Select(item =>
            {
                var path = Normalize(item.FilePath);
                var deviceFolder = DeviceFolderFromPath(path);
                var device = masterDevices.FirstOrDefault(x =>
                    string.Equals(
                        Path.GetFileName(x.Context.DeviceRoot),
                        deviceFolder,
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Metadata.PlcName, deviceFolder, StringComparison.OrdinalIgnoreCase));
                var deviceId = device.Metadata is null ? deviceFolder : device.Metadata.DeviceId;
                var plcName = device.Metadata is null ? deviceFolder : device.Metadata.PlcName;
                var lifecycle = preview.FeaturePaths.Any(x => Normalize(x) == path)
                    && File.Exists(WorkbenchPaths.ResolveRelative(masterRoot, path))
                    ? null
                    : "SOURCE_LIFECYCLE_UNSUPPORTED";
                var reason = device.Metadata is null
                    ? "DEVICE_NOT_FOUND"
                    : lifecycle ?? (tiaPaths.Contains(path) ? "TIA_FEATURE_OVERLAP" : null);
                return new FeatureImportObject(
                    deviceId,
                    plcName,
                    path,
                    item.Sha256,
                    reason is null,
                    reason);
            })
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToList();
        foreach (var path in preview.FeaturePaths.Select(Normalize).Where(IsManagedSource))
        {
            if (objects.Any(item => item.RelativePath == path)) continue;
            var deviceFolder = DeviceFolderFromPath(path);
            var device = masterDevices.FirstOrDefault(x =>
                string.Equals(
                    Path.GetFileName(x.Context.DeviceRoot),
                    deviceFolder,
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Metadata.PlcName, deviceFolder, StringComparison.OrdinalIgnoreCase));
            var deviceId = device.Metadata is null ? deviceFolder : device.Metadata.DeviceId;
            var plcName = device.Metadata is null ? deviceFolder : device.Metadata.PlcName;
            objects.Add(new FeatureImportObject(deviceId, plcName, path, string.Empty, false, "SOURCE_LIFECYCLE_UNSUPPORTED"));
        }

        var plan = new FeatureImportPlan(
            Guid.NewGuid().ToString("N"),
            workbench.WorkbenchId,
            feature.WorktreeId,
            featureSha,
            masterSha,
            masterComparison.ComparisonId,
            objects.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray());
        store.Write(PlanPath(workbench, plan.PlanId), plan);
        return plan;
    }

    public FeatureImportPlan ReadPlan(WorkbenchMetadata workbench, string planId) =>
        store.Read<FeatureImportPlan>(PlanPath(workbench, planId));

    public async Task<FeatureImportSession> ImportAsync(
        WorkbenchMetadata workbench,
        string planId,
        IReadOnlyList<string> selectedPaths,
        CancellationToken cancellationToken = default)
    {
        var plan = ReadPlan(workbench, planId);
        var selected = selectedPaths.Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();
        var chosen = plan.Objects.Where(item => selected.Contains(item.RelativePath, StringComparer.Ordinal)).ToArray();
        if (chosen.Length != selected.Length)
            throw new WorkbenchLifecycleException("SOURCE_NOT_IN_IMPORT_PLAN", "Every selected source must belong to the import plan.");
        if (chosen.Any(item => !item.Importable))
            throw new WorkbenchLifecycleException("SOURCE_NOT_IMPORTABLE", "The selection includes an overlapping or unsupported source.");

        var master = LoadMaster(workbench);
        var featureRoot = ResolveWorktreeRoot(workbench, plan.FeatureWorktreeId);
        var contexts = LoadDevices(workbench, master).ToDictionary(item => item.Metadata.DeviceId, StringComparer.Ordinal);
        var outcomes = new List<FeatureImportOutcome>(chosen.Length);
        foreach (var item in chosen)
        {
            try
            {
                if (!contexts.TryGetValue(item.DeviceId, out var device))
                    throw new WorkbenchLifecycleException("DEVICE_NOT_FOUND", $"Device '{item.DeviceId}' was not found.");
                var source = WorkbenchPaths.ResolveRelative(featureRoot, item.RelativePath);
                var result = await engineering.CallAsync<SourceObjectImportResult>(
                    "import_source_object",
                    new { relativePath = ExtractSourcePath(item.RelativePath), xmlFilePath = source, plcName = item.PlcName },
                    cancellationToken).ConfigureAwait(false);
                outcomes.Add(new FeatureImportOutcome(
                    item.DeviceId,
                    item.RelativePath,
                    result.Success ? FeatureImportState.Imported : FeatureImportState.Failed,
                    result.Error,
                    result.Warnings ?? Array.Empty<string>()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcomes.Add(new FeatureImportOutcome(item.DeviceId, item.RelativePath, FeatureImportState.Failed, ex.Message, Array.Empty<string>()));
            }
        }

        var session = new FeatureImportSession(
            Guid.NewGuid().ToString("N"),
            plan.PlanId,
            plan.FeatureSha,
            plan.MasterSha,
            DateTimeOffset.UtcNow.ToString("O"),
            outcomes);
        store.Write(SessionPath(workbench, session.SessionId), session);
        return session;
    }

    public FeatureImportSession ReadSession(WorkbenchMetadata workbench, string sessionId) =>
        store.Read<FeatureImportSession>(SessionPath(workbench, sessionId));

    public async Task<FeatureImportSession> RollbackAsync(
        WorkbenchMetadata workbench,
        string sessionId,
        IReadOnlyList<string> selectedPaths,
        CancellationToken cancellationToken = default)
    {
        var session = ReadSession(workbench, sessionId);
        var selected = selectedPaths.Select(Normalize).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var master = LoadMaster(workbench);
        var masterRoot = ResolveWorktreeRoot(workbench, master.WorktreeId);
        var updated = session.Objects.ToList();
        foreach (var item in session.Objects.Where(item => selected.Contains(item.RelativePath) && item.State == FeatureImportState.Imported))
        {
            var source = WorkbenchPaths.ResolveRelative(masterRoot, item.RelativePath);
            try
            {
                var result = await engineering.CallAsync<SourceObjectImportResult>(
                    "import_source_object",
                    new { relativePath = ExtractSourcePath(item.RelativePath), xmlFilePath = source, plcName = PlcNameFor(workbench, master, item.DeviceId) },
                    cancellationToken).ConfigureAwait(false);
                var index = updated.FindIndex(candidate => candidate.RelativePath == item.RelativePath);
                updated[index] = item with
                {
                    State = result.Success ? FeatureImportState.RolledBack : FeatureImportState.Failed,
                    Error = result.Error,
                    Warnings = result.Warnings ?? Array.Empty<string>(),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var index = updated.FindIndex(candidate => candidate.RelativePath == item.RelativePath);
                updated[index] = item with { State = FeatureImportState.Failed, Error = ex.Message };
            }
        }

        var resultSession = session with { Objects = updated };
        store.Write(SessionPath(workbench, sessionId), resultSession);
        return resultSession;
    }

    public FeatureImportSession KeepAfterCompileFailure(
        WorkbenchMetadata workbench,
        string sessionId,
        IReadOnlyList<string> selectedPaths)
    {
        var session = ReadSession(workbench, sessionId);
        var selected = selectedPaths.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var updated = session.Objects
            .Select(item => selected.Contains(item.RelativePath) && item.State == FeatureImportState.Imported
                ? item with { State = FeatureImportState.KeptAfterCompileFailure }
                : item)
            .ToArray();
        var result = session with { Objects = updated };
        store.Write(SessionPath(workbench, sessionId), result);
        return result;
    }

    private WorktreeMetadata LoadMaster(WorkbenchMetadata workbench)
    {
        var registration = workbench.Worktrees.Single(item => string.Equals(item.Branch, "master", StringComparison.OrdinalIgnoreCase));
        return store.Read<WorktreeMetadata>(Path.Combine(ResolveWorktreeRoot(workbench, registration.WorktreeId), "worktree.json"));
    }

    private IReadOnlyList<(DeviceMetadata Metadata, DeviceContext Context)> LoadDevices(WorkbenchMetadata workbench, WorktreeMetadata worktree)
    {
        var root = ResolveWorktreeRoot(workbench, worktree.WorktreeId);
        var devicesRoot = WorkbenchPaths.ResolveRelative(root, "devices");
        var registration = workbench.Worktrees.Single(item => item.WorktreeId == worktree.WorktreeId);
        var metadataById = Directory.EnumerateDirectories(devicesRoot)
            .Select(directory => Path.Combine(directory, "device.json"))
            .Where(File.Exists)
            .Select(store.TryRead<DeviceMetadata>)
            .Where(metadata => metadata is not null)
            .Cast<DeviceMetadata>()
            .ToDictionary(metadata => metadata.DeviceId, StringComparer.Ordinal);

        return worktree.DeviceIds.Select(id =>
        {
            if (!metadataById.TryGetValue(id, out var metadata))
                throw new WorkbenchCatalogException("DEVICE_NOT_FOUND", $"Device '{id}' was not found in worktree '{worktree.WorktreeId}'.");

            var context = WorkbenchPaths.ResolveDevice(
                workbench.WorkbenchId,
                workbench.RootPath,
                worktree.WorktreeId,
                registration.RelativePath,
                id,
                metadata.PlcName);
            return (metadata, context);
        }).ToArray();
    }

    private string ResolveWorktreeRoot(WorkbenchMetadata workbench, string worktreeId)
    {
        var registration = workbench.Worktrees.Single(item => item.WorktreeId == worktreeId);
        return WorkbenchPaths.ResolveWorktree(workbench.RootPath, registration.RelativePath);
    }

    private string PlanPath(WorkbenchMetadata workbench, string id) => Path.Combine(workbench.RootPath, ".automation", "import-plans", id + ".json");
    private string SessionPath(WorkbenchMetadata workbench, string id) => Path.Combine(workbench.RootPath, ".automation", "import-sessions", id + ".json");
    private string PlcNameFor(WorkbenchMetadata workbench, WorktreeMetadata worktree, string deviceId)
    {
        return LoadDevices(workbench, worktree)
            .FirstOrDefault(item => string.Equals(item.Metadata.DeviceId, deviceId, StringComparison.Ordinal))
            .Metadata?.PlcName ?? deviceId;
    }
    private static bool IsManagedSource(string path) => Normalize(path).StartsWith("devices/", StringComparison.OrdinalIgnoreCase) && Normalize(path).Contains("/source/", StringComparison.OrdinalIgnoreCase) && Normalize(path).EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
    private static string DeviceFolderFromPath(string path) => Normalize(path).Split('/').Skip(1).FirstOrDefault() ?? string.Empty;
    private static string ExtractSourcePath(string path) => Normalize(path).Split("/source/", 2, StringSplitOptions.None).Last();
}
