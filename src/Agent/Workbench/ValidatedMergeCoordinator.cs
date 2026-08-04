using Agent.Mcp;
using Contracts.Engineering;

namespace Agent.Workbench;

public sealed record ValidateFeatureMergeRequest(
    string WorkbenchId,
    string FeatureWorktreeId,
    string ImportSessionId,
    bool MachineValidated,
    string ConfirmedBy);

public enum ValidatedMergeState
{
    Ready,
    CompileFailed,
    SourceDifferent,
    BranchMoved,
}

public sealed record ValidatedMergeDevice(
    string DeviceId,
    string PlcName,
    string ProjectIdentity,
    string ProjectChecksum,
    IReadOnlyList<VcValidationObject> Objects);

public sealed record VcValidationObject(string Identity, string RelativePath, string Sha256);

public sealed record ValidatedMergeDraft(
    string ValidationId,
    string WorkbenchId,
    string FeatureWorktreeId,
    string ImportSessionId,
    string TargetBranch,
    string SourceBranch,
    string TargetSha,
    string SourceSha,
    string CandidateTreeSha,
    string ConfirmedAt,
    string ConfirmedBy,
    IReadOnlyList<ValidatedMergeDevice> Devices);

public sealed record ValidatedMergeResult(
    string ValidationId,
    ValidatedMergeState State,
    string? Error,
    IReadOnlyList<ValidatedMergeDevice> Devices);

public sealed class FeatureMergeEvidenceDto
{
    public string SchemaVersion { get; set; } = "1.0";
    public string EvidenceKind { get; set; } = "feature-merge";
    public string CommitSha { get; set; } = string.Empty;
    public string WorkbenchId { get; set; } = string.Empty;
    public string? SourceWorktreeId { get; set; }
    public string ConfirmedAt { get; set; } = string.Empty;
    public string ConfirmedBy { get; set; } = string.Empty;
    public bool MachineValidated { get; set; }
    public FeatureMergeEvidenceDeviceDto[] Devices { get; set; } = Array.Empty<FeatureMergeEvidenceDeviceDto>();
}

public sealed class FeatureMergeEvidenceDeviceDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string PlcName { get; set; } = string.Empty;
    public string ProjectIdentity { get; set; } = string.Empty;
    public string ProjectChecksum { get; set; } = string.Empty;
    public FeatureMergeEvidenceObjectDto[] Objects { get; set; } = Array.Empty<FeatureMergeEvidenceObjectDto>();
}

public sealed class FeatureMergeEvidenceObjectDto
{
    public string Identity { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

public sealed record FeatureMergePublicationResult(bool Merged, string Sha, FeatureMergeEvidenceDto Evidence, string ValidationTag);

/// <summary>Runs the all-device compile and exact prospective-source gate.</summary>
public sealed class ValidatedMergeCoordinator
{
    private readonly IMcpToolCaller engineering;
    private readonly IMcpToolCaller versionControl;
    private readonly AtomicJsonStore store;
    private readonly PlcSourceScanner scanner;

    public ValidatedMergeCoordinator(
        IMcpToolCaller engineering,
        IMcpToolCaller versionControl,
        AtomicJsonStore? store = null)
    {
        this.engineering = engineering ?? throw new ArgumentNullException(nameof(engineering));
        this.versionControl = versionControl ?? throw new ArgumentNullException(nameof(versionControl));
        this.store = store ?? new AtomicJsonStore();
        scanner = new PlcSourceScanner(engineering);
    }

    public async Task<ValidatedMergeResult> ValidateAsync(
        WorkbenchMetadata workbench,
        WorktreeMetadata feature,
        FeatureImportSession session,
        ValidateFeatureMergeRequest request,
        CancellationToken cancellationToken = default,
        IOperationProgress? progress = null)
    {
        if (!request.MachineValidated)
            throw new WorkbenchLifecycleException("MACHINE_CONFIRMATION_REQUIRED", "Confirm that the complete PLC project compiled and was tested on the machine.");
        if (string.IsNullOrWhiteSpace(request.ConfirmedBy))
            throw new WorkbenchLifecycleException("CONFIRMED_BY_REQUIRED", "A confirming identity is required.");

        var master = LoadMaster(workbench);
        var masterRoot = ResolveRoot(workbench, master.WorktreeId);
        var featureRoot = ResolveRoot(workbench, feature.WorktreeId);
        var preview = await versionControl.CallAsync<FeaturePreviewDto>(
            "vc_merge_preview", new { repoPath = masterRoot, sourceBranch = feature.Branch }, cancellationToken).ConfigureAwait(false);
        if (preview.HasConflicts)
            throw new WorkbenchLifecycleException("GIT_MERGE_CONFLICT", "The feature cannot be validated while its prospective merge has conflicts.");
        if (string.IsNullOrWhiteSpace(preview.CandidateTreeSha))
            throw new WorkbenchLifecycleException("CANDIDATE_TREE_UNAVAILABLE", "The prospective merge did not produce a candidate tree.");

        var featureLog = await HeadAsync(featureRoot, cancellationToken).ConfigureAwait(false);
        var masterLog = await HeadAsync(masterRoot, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(featureLog, session.FeatureSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(masterLog, session.MasterSha, StringComparison.OrdinalIgnoreCase))
            return new ValidatedMergeResult(string.Empty, ValidatedMergeState.BranchMoved, "The feature or master branch moved after import.", Array.Empty<ValidatedMergeDevice>());

        var expectedPaths = preview.FeaturePaths
            .Select(Normalize)
            .Where(IsManagedSource)
            .ToHashSet(StringComparer.Ordinal);
        var imported = session.Objects
            .Where(item => item.State == FeatureImportState.Imported || item.State == FeatureImportState.KeptAfterCompileFailure)
            .Select(item => Normalize(item.RelativePath))
            .ToHashSet(StringComparer.Ordinal);
        var missing = expectedPaths.Except(imported, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
            throw new WorkbenchLifecycleException("IMPORT_INCOMPLETE", $"The validation session does not contain successful imports for: {string.Join(", ", missing)}.");

        var devices = LoadDevices(workbench, master);
        var compileFailed = false;
        foreach (var device in devices)
        {
            progress?.Report($"Compiling {device.Metadata.PlcName}...");
            var compile = await engineering.CallAsync<CompileResult>("compile_plc", new { plcName = device.Metadata.PlcName }, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(compile.State, "success", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(compile.State, "warnings", StringComparison.OrdinalIgnoreCase))
                compileFailed = true;
        }
        if (compileFailed)
            return new ValidatedMergeResult(string.Empty, ValidatedMergeState.CompileFailed, "One or more PLC devices failed compilation.", Array.Empty<ValidatedMergeDevice>());

        var validatedDevices = new List<ValidatedMergeDevice>(devices.Count);
        foreach (var device in devices)
        {
            progress?.Report($"Scanning {device.Metadata.PlcName}...");
            var scan = await scanner.ScanAsync(device.Context, cancellationToken, progress, device.Metadata.PlcName).ConfigureAwait(false);
            if (scan.UnsupportedObjects.Count > 0)
                throw new WorkbenchLifecycleException("SOURCE_COVERAGE_INCOMPLETE", $"TIA source coverage is incomplete for '{device.Metadata.PlcName}'.");

            var expected = preview.Objects
                .Where(item => IsDeviceSource(item.FilePath, device.Metadata.DeviceId))
                .ToDictionary(item => SourceRelative(item.FilePath, device.Metadata.DeviceId), item => item.Sha256, StringComparer.Ordinal);
            var actual = scan.Objects.ToDictionary(item => Normalize(item.RelativePath), item => item.Sha256, StringComparer.Ordinal);
            if (!expected.OrderBy(item => item.Key).SequenceEqual(actual.OrderBy(item => item.Key)))
                return new ValidatedMergeResult(string.Empty, ValidatedMergeState.SourceDifferent, $"TIA source differs from the prospective merge for '{device.Metadata.PlcName}'.", Array.Empty<ValidatedMergeDevice>());

            validatedDevices.Add(new ValidatedMergeDevice(
                device.Metadata.DeviceId,
                device.Metadata.PlcName,
                scan.ProjectIdentity,
                scan.ProjectChecksum,
                scan.Objects.Select(item => new VcValidationObject(item.Identity, $"devices/{device.Metadata.DeviceId}/source/{Normalize(item.RelativePath)}", item.Sha256)).ToArray()));
        }

        var currentFeature = await HeadAsync(featureRoot, cancellationToken).ConfigureAwait(false);
        var currentMaster = await HeadAsync(masterRoot, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentFeature, featureLog, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(currentMaster, masterLog, StringComparison.OrdinalIgnoreCase))
            return new ValidatedMergeResult(string.Empty, ValidatedMergeState.BranchMoved, "The feature or master branch moved during validation.", Array.Empty<ValidatedMergeDevice>());

        var validationId = Guid.NewGuid().ToString("N");
        var draft = new ValidatedMergeDraft(validationId, workbench.WorkbenchId, feature.WorktreeId, session.SessionId,
            "master", feature.Branch, masterLog, featureLog, preview.CandidateTreeSha!, DateTimeOffset.UtcNow.ToString("O"), request.ConfirmedBy, validatedDevices);
        store.Write(Path.Combine(workbench.RootPath, ".automation", "validated-merges", validationId + ".json"), draft);
        return new ValidatedMergeResult(validationId, ValidatedMergeState.Ready, null, validatedDevices);
    }

    public ValidatedMergeDraft ReadDraft(WorkbenchMetadata workbench, string validationId) =>
        store.Read<ValidatedMergeDraft>(Path.Combine(workbench.RootPath, ".automation", "validated-merges", validationId + ".json"));

    private async Task<string> HeadAsync(string root, CancellationToken token)
    {
        var log = await versionControl.CallAsync<ConsistencyLogResult>("vc_log", new { repoPath = root, maxCount = 1 }, token).ConfigureAwait(false);
        return log.Commits.FirstOrDefault()?.Sha ?? throw new WorkbenchLifecycleException("HEAD_UNAVAILABLE", "The worktree has no Git HEAD.");
    }

    private WorktreeMetadata LoadMaster(WorkbenchMetadata workbench)
    {
        var registration = workbench.Worktrees.Single(item => string.Equals(item.Branch, "master", StringComparison.OrdinalIgnoreCase));
        return store.Read<WorktreeMetadata>(Path.Combine(ResolveRoot(workbench, registration.WorktreeId), "worktree.json"));
    }

    private IReadOnlyList<(DeviceMetadata Metadata, DeviceContext Context)> LoadDevices(WorkbenchMetadata workbench, WorktreeMetadata master)
    {
        var root = ResolveRoot(workbench, master.WorktreeId);
        return master.DeviceIds.Select(id =>
        {
            var deviceRoot = WorkbenchPaths.ResolveRelative(root, $"devices/{id}");
            var metadata = store.Read<DeviceMetadata>(Path.Combine(deviceRoot, "device.json"));
            return (metadata, new DeviceContext(workbench.WorkbenchId, master.WorktreeId, id, workbench.RootPath, root, deviceRoot,
                WorkbenchPaths.ResolveRelative(deviceRoot, "source"), WorkbenchPaths.ResolveRelative(deviceRoot, "staging"), WorkbenchPaths.ResolveRelative(deviceRoot, "plc-knowledge.db")));
        }).ToArray();
    }

    private static string ResolveRoot(WorkbenchMetadata workbench, string worktreeId) =>
        WorkbenchPaths.ResolveWorktree(workbench.RootPath, workbench.Worktrees.Single(item => item.WorktreeId == worktreeId).RelativePath);
    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
    private static bool IsManagedSource(string path) => Normalize(path).StartsWith("devices/", StringComparison.OrdinalIgnoreCase) && Normalize(path).Contains("/source/", StringComparison.OrdinalIgnoreCase) && Normalize(path).EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    private static bool IsDeviceSource(string path, string deviceId) => Normalize(path).StartsWith($"devices/{deviceId}/source/", StringComparison.Ordinal);
    private static string SourceRelative(string path, string deviceId) => Normalize(path).Split($"devices/{deviceId}/source/", 2, StringSplitOptions.None).Last();
}
