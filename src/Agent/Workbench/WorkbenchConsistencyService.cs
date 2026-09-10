using Agent.Mcp;
using Contracts.Engineering;
using System.Diagnostics;

namespace Agent.Workbench;

public sealed class ConsistencyLogResult
{
    public ConsistencyCommit[] Commits { get; set; } = Array.Empty<ConsistencyCommit>();
}

public sealed class ConsistencyCommit
{
    public string Sha { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public string[] Files { get; set; } = Array.Empty<string>();
}

public sealed class ConsistencyValidationEvidence
{
    public string SchemaVersion { get; set; } = string.Empty;
    public string EvidenceKind { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public bool? ManagedSourceConsistent { get; set; }
    public ConsistencyValidationDevice[] Devices { get; set; } = Array.Empty<ConsistencyValidationDevice>();
}

public sealed class ConsistencyValidationDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public string PlcName { get; set; } = string.Empty;
    public string ProjectChecksum { get; set; } = string.Empty;
    public ManagedSourceEvidenceObject[]? SourceEvidence { get; set; }
}

public sealed class ConsistencyStatusResult
{
    public ConsistencyStatusEntry[] Entries { get; set; } = Array.Empty<ConsistencyStatusEntry>();
}

public sealed class ConsistencyStatusEntry
{
    public string FilePath { get; set; } = string.Empty;
}

public sealed class TiaSyncEvidence
{
    public string SchemaVersion { get; set; } = "1.0";
    public string EvidenceKind { get; set; } = "tia-sync";
    public string CommitSha { get; set; } = string.Empty;
    public string WorkbenchId { get; set; } = string.Empty;
    public string? SourceWorktreeId { get; set; }
    public string ConfirmedAt { get; set; } = string.Empty;
    public string ConfirmedBy { get; set; } = string.Empty;
    public bool MachineValidated { get; set; }
    /// <summary>Schema v2 verdict for the Git-managed portion of the TIA project. Native-only
    /// changes remain separately represented by their timeline/savepoint evidence.</summary>
    public bool? ManagedSourceConsistent { get; set; }
    public IReadOnlyList<TiaSyncEvidenceDevice> Devices { get; set; } = Array.Empty<TiaSyncEvidenceDevice>();
}

public sealed class TiaSyncEvidenceDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public string PlcName { get; set; } = string.Empty;
    public string ProjectIdentity { get; set; } = string.Empty;
    public string ProjectChecksum { get; set; } = string.Empty;
    public IReadOnlyList<TiaSyncEvidenceObject> Objects { get; set; } = Array.Empty<TiaSyncEvidenceObject>();
    public IReadOnlyList<ManagedSourceEvidenceObject>? SourceEvidence { get; set; }
}

public sealed class TiaSyncEvidenceObject
{
    public string Identity { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>Compares the registered master source tree with the live TIA project.</summary>
public sealed class WorkbenchConsistencyService
{
    private readonly IMcpToolCaller engineering;
    private readonly IMcpToolCaller versionControl;
    private readonly WorkbenchCatalog catalog;
    private readonly AtomicJsonStore store;
    private readonly PlcSourceScanner scanner;

    public WorkbenchConsistencyService(
        IMcpToolCaller engineering,
        IMcpToolCaller versionControl,
        WorkbenchCatalog? catalog = null,
        AtomicJsonStore? store = null)
    {
        this.engineering = engineering ?? throw new ArgumentNullException(nameof(engineering));
        this.versionControl = versionControl ?? throw new ArgumentNullException(nameof(versionControl));
        this.catalog = catalog ?? new WorkbenchCatalog();
        this.store = store ?? new AtomicJsonStore();
        scanner = new PlcSourceScanner(engineering);
    }

    public async Task<WorkbenchConsistencyResult> CompareAsync(
        WorkbenchMetadata workbench,
        WorktreeMetadata master,
        CancellationToken cancellationToken = default,
        IOperationProgress? progress = null,
        bool allowCompile = false,
        bool forceFullExport = false,
        bool includeHardware = true,
        string? comparedWorktreeId = null)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        ArgumentNullException.ThrowIfNull(master);
        if (!string.Equals(workbench.WorkbenchId, master.WorkbenchId, StringComparison.Ordinal))
            throw new WorkbenchCatalogException("WORKBENCH_RELATIONSHIP_MISMATCH", "The master worktree belongs to another workbench.");

        var timings = new List<ComparisonTiming>();
        var masterRoot = ResolveMasterRoot(workbench, master);
        var head = await MeasureAsync(
                timings,
                "master-head",
                "Read the master commit that supplies the comparison baseline.",
                null,
                () => ReadHeadAsync(masterRoot, cancellationToken),
                result => $"Comparing against {result.Sha[..Math.Min(7, result.Sha.Length)]}.")
            .ConfigureAwait(false);
        HardwareConfigurationCompareResult? hardware;
        if (includeHardware)
        {
            hardware = await MeasureAsync(
                    timings,
                    "hardware-export",
                    "Export and compare project AML plus the network configuration fingerprint.",
                    null,
                    () => CompareHardwareAsync(masterRoot, cancellationToken, progress, timings),
                    DescribeHardwareOutcome)
                .ConfigureAwait(false);
        }
        else
        {
            timings.Add(new ComparisonTiming(
                "hardware-export",
                "Export and compare project AML plus the network configuration fingerprint.",
                null,
                0,
                "Hardware verification was not checked."));
            hardware = null;
        }
        var hardwareMatches = !includeHardware || hardware?.State == "in-sync";
        var evidence = await MeasureAsync(
                timings,
                "validation-evidence-read",
                "Read exact TIA validation evidence attached to the current master commit.",
                null,
                () => versionControl.CallAsync<ConsistencyValidationEvidence?>(
                    "vc_validation_get",
                    new { repoPath = masterRoot, commitSha = head.Sha },
                    cancellationToken),
                result => result is null ? "No evidence for this master commit." : $"Evidence covers {result.Devices.Length} PLC device(s).")
            .ConfigureAwait(false);
        var untrackableChange = await MeasureAsync(
                timings,
                "untrackable-change-read",
                "Check whether a message-only TIA change prevents trusting the checksum fast path.",
                null,
                () => versionControl.CallAsync<TimelineUntrackableChangeResult>(
                    "vc_untrackable_change_get",
                    new { repoPath = masterRoot, commitSha = head.Sha },
                    cancellationToken),
                result => result.UntrackableChange ? "Untrackable change is pending." : "No untrackable change is pending.")
            .ConfigureAwait(false);
        var status = await MeasureAsync(
                timings,
                "master-status-read",
                "Check whether master has uncommitted managed source XML that invalidates a fast comparison.",
                null,
                () => versionControl.CallAsync<ConsistencyStatusResult>(
                    "vc_status",
                    new { repoPath = masterRoot },
                    cancellationToken),
                result => $"{result.Entries.Length} changed repository path(s).")
            .ConfigureAwait(false);
        var sourceClean = !status.Entries.Any(entry => IsManagedSourceXml(entry.FilePath));
        var devices = Measure(
            timings,
            "device-baseline-load",
            "Load the registered master devices and their source/staging roots.",
            null,
            () => LoadDevices(workbench, master),
            result => $"Loaded {result.Count} PLC device(s).");
        if (allowCompile)
        {
            await MeasureAsync(
                    timings,
                    "project-save-before-compile",
                    "Save TIA before the user-approved automatic compilation pass.",
                    null,
                    () => engineering.CallAsync<object>("save_project", new { }, cancellationToken),
                    _ => "TIA project saved.")
                .ConfigureAwait(false);
            foreach (var device in devices)
            {
                var compile = await MeasureAsync(
                        timings,
                        "plc-compile",
                        "Compile the PLC because automatic compilation was explicitly approved for this compare.",
                        device.Metadata.PlcName,
                        () => engineering.CallAsync<CompileResult>("compile_plc", new { plcName = device.Metadata.PlcName }, cancellationToken),
                        result => $"Compile state: {result.State}.")
                    .ConfigureAwait(false);
                if (string.Equals(compile.State, "error", StringComparison.OrdinalIgnoreCase))
                    throw new WorkbenchLifecycleException("PLC_COMPILE_FAILED", $"Automatic PLC compile failed for '{device.Metadata.PlcName}'.");
            }
        }
        if (!forceFullExport
            && sourceClean
            && !untrackableChange.UntrackableChange
            && HasChecksumEvidence(evidence, head, devices))
        {
            // A compiled PLC software checksum covers all managed software changes, including
            // comments and interface edits.  Probe it before the expensive fingerprint walk so
            // an unchanged project can use the same fast gate as legacy validation evidence.
            var fingerprintChecksums = await MeasureAsync(
                    timings,
                    "checksum-read",
                    "Read every PLC software checksum before deciding whether source evidence must be scanned.",
                    null,
                    () => engineering.CallAsync<PlcChecksumInfo[]>("get_plc_checksums", new { }, cancellationToken),
                    result => $"Read checksum evidence for {result.Length} PLC device(s).")
                .ConfigureAwait(false);
            var fingerprintLiveChecksums = devices.ToDictionary(
                item => item.Metadata.DeviceId,
                item => fingerprintChecksums.FirstOrDefault(checksum =>
                    string.Equals(checksum.PlcName, item.Metadata.PlcName, StringComparison.OrdinalIgnoreCase)),
                StringComparer.Ordinal);
            var fingerprintChecksumsMatch = devices.All(item =>
            {
                var live = fingerprintLiveChecksums[item.Metadata.DeviceId];
                var expected = evidence!.Devices.FirstOrDefault(device =>
                    string.Equals(device.DeviceId, item.Metadata.DeviceId, StringComparison.Ordinal));
                return live is not null
                    && live.IsCompiled
                    && !string.IsNullOrWhiteSpace(live.SoftwareChecksum)
                    && expected is not null
                    && string.Equals(expected.PlcName, item.Metadata.PlcName, StringComparison.Ordinal)
                    && string.Equals(expected.ProjectChecksum, live.SoftwareChecksum, StringComparison.Ordinal);
            });
            var fingerprintHasSafetySurface = fingerprintChecksums.Any(checksum =>
                    checksum.IsSafetyDevice == true
                    || checksum.FSignatureReadState is not null
                    || checksum.FSignature is not null
                    || checksum.FBlockSignatures is not null)
                || evidence!.Devices.Any(device =>
                    device.SourceEvidence?.Any(source =>
                        string.Equals(source.Kind, ManagedSourceEvidenceKind.FBlock, StringComparison.Ordinal)
                        || source.FSignature is not null) == true);
            if (fingerprintChecksumsMatch && !fingerprintHasSafetySurface)
            {
                var fingerprintState = hardwareMatches
                    ? ConsistencyState.Consistent
                    : ConsistencyState.Different;
                return Persist(workbench, new WorkbenchConsistencyResult(
                    Guid.NewGuid().ToString("N"),
                    head.Sha,
                    true,
                    fingerprintState,
                    fingerprintLiveChecksums.ToDictionary(
                        item => item.Key,
                        item => item.Value?.SoftwareChecksum,
                        StringComparer.Ordinal),
                    Array.Empty<SourceDifference>(),
                    hardware,
                    Array.Empty<DeviceSafetyEvidence>(),
                    false,
                    timings.ToArray(),
                    HardwareChecked: includeHardware,
                    ComparedWorktreeId: comparedWorktreeId));
            }

            return await CompareFingerprintFirstAsync(
                    workbench,
                    master,
                    head,
                    hardware,
                    includeHardware,
                    evidence!,
                    devices,
                    untrackableChange.UntrackableChange,
                    timings,
                    comparedWorktreeId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        var checksums = await MeasureAsync(
                timings,
                "checksum-read",
                "Read every PLC software checksum and safety signature evidence from TIA.",
                null,
                () => engineering.CallAsync<PlcChecksumInfo[]>("get_plc_checksums", new { }, cancellationToken),
                result => $"Read checksum evidence for {result.Length} PLC device(s).")
            .ConfigureAwait(false);
        var liveChecksums = devices.ToDictionary(
            item => item.Metadata.DeviceId,
            item => checksums.FirstOrDefault(checksum =>
                string.Equals(checksum.PlcName, item.Metadata.PlcName, StringComparison.OrdinalIgnoreCase))?.SoftwareChecksum,
            StringComparer.Ordinal);

        // Safety evidence: the offline collective F-signature is read for every PLC on every
        // compare (get_plc_checksums above) and checked against the safety baseline recorded in
        // the device's git-tracked source manifest (legacy workbenches: master's revision.json).
        // A changed
        // signature must make the result non-consistent even when checksums and XML are unchanged;
        // a failed required read must never pass as consistent. Three distinct situations:
        // - live signature present and different (or newly appearing) -> Changed.
        // - baseline signature present but the live device no longer reports a safety surface
        //   (F-CPU replaced by a standard CPU / safety program deleted) -> Changed.
        // - baseline signature present, live still a safety device, but no live signature
        //   (no F-block exposed a BlockOfflineSignature in this session, e.g. the safety
        //   program was never compiled here) -> degraded evidence, Unavailable below,
        //   never a phantom change.
        // When both sides recorded per-F-block signatures, the change is additionally attributed
        // to individual blocks (ChangedBlocks); a 0 signature means "missing or invalidated by a
        // recent change" (TIA Openness manual §5.27.4) and diffs like any other value.
        var legacyBaselineSafety = ReadBaselineSafety(masterRoot);
        var safety = devices.Select(item =>
            {
                var live = checksums.FirstOrDefault(checksum =>
                    string.Equals(checksum.PlcName, item.Metadata.PlcName, StringComparison.OrdinalIgnoreCase));
                // Baseline: the device's git-tracked source manifest, advanced by every
                // safety-accepting commit; workbenches whose manifests predate safety data fall
                // back to master's revision.json.
                var baseline = DeviceManifestSafety.TryReadBaseline(item.Context.SourceRoot) is { } manifestBaseline
                    ? new BaselineSafety(manifestBaseline.FSignature, manifestBaseline.BlockSignatures)
                    : legacyBaselineSafety.TryGetValue(item.Metadata.PlcName, out var legacy)
                        ? legacy
                        : null;
                var blockDifferences = DiffFBlockSignatures(baseline?.BlockSignatures, live?.FBlockSignatures);
                var changedBlocks = blockDifferences?.Select(item => item.Path).ToArray();
                return new DeviceSafetyEvidence(
                    item.Metadata.DeviceId,
                    item.Metadata.PlcName,
                    live?.IsSafetyDevice == true,
                    live?.FSignatureReadState,
                    live?.FSignature,
                    baseline?.FSignature,
                    Changed: changedBlocks is not null
                        ? changedBlocks.Length > 0
                        : (live?.FSignature is not null
                            && !string.Equals(live.FSignature, baseline?.FSignature ?? string.Empty, StringComparison.Ordinal))
                        || (live?.IsSafetyDevice != true && baseline?.FSignature is not null),
                    live?.FBlockSignatures,
                    changedBlocks,
                    blockDifferences);
            })
            .ToArray();
        var safetyChanged = safety.Any(item => item.Changed);
        var safetyReadFailed = safety.Any(item =>
            item.IsSafetyDevice
            && (string.Equals(item.ReadState, FSignatureReadState.ReadFailed, StringComparison.Ordinal)
                || (item.BaselineFSignature is not null && item.FSignature is null)));

        // A message-only untrackable commit records the live TIA checksum but no source
        // content. It must not certify the source tree for the checksum fast path; otherwise a
        // pending trackable TIA diff remains invisible until a later checksum change.
        var evidenceCurrent = !untrackableChange.UntrackableChange
            && evidence is not null
            // Legacy evidence predates this field and remains usable; an explicit false is the
            // savepoint's capture-only marker and must never certify the source tree.
            && evidence.ManagedSourceConsistent is not false
            && string.Equals(evidence.CommitSha, head.Sha, StringComparison.OrdinalIgnoreCase)
            && evidence.Devices.Length == devices.Count;
        var checksumsMatch = evidenceCurrent && devices.All(item =>
        {
            var expected = evidence!.Devices.FirstOrDefault(device =>
                string.Equals(device.DeviceId, item.Metadata.DeviceId, StringComparison.Ordinal));
            return expected is not null
                && string.Equals(expected.PlcName, item.Metadata.PlcName, StringComparison.Ordinal)
                && string.Equals(expected.ProjectChecksum, liveChecksums[item.Metadata.DeviceId], StringComparison.Ordinal);
        });

        if (evidenceCurrent && sourceClean && checksumsMatch)
        {
            var fastState = safetyChanged || !hardwareMatches
                ? ConsistencyState.Different
                : safetyReadFailed
                    ? ConsistencyState.Unavailable
                    : ConsistencyState.Consistent;
            return Persist(workbench, new WorkbenchConsistencyResult(
                Guid.NewGuid().ToString("N"),
                head.Sha,
                true,
                fastState,
                liveChecksums,
                Array.Empty<SourceDifference>(),
                hardware,
                safety,
                safetyChanged,
                timings.ToArray(),
                HardwareChecked: includeHardware,
                ComparedWorktreeId: comparedWorktreeId));
        }

        var evidenceSourceCanNarrow = evidenceCurrent && sourceClean;
        var devicesToScan = evidenceSourceCanNarrow
            ? devices.Where(item => !ChecksumMatches(evidence!, item.Metadata, liveChecksums[item.Metadata.DeviceId])).ToArray()
            : devices.ToArray();
        // The previous export manifest knows how many XML files each device produced, so the
        // per-device "Exported PLC source files: N" counters can be surfaced as an overall
        // "current of total" for the whole compare (best effort: no manifest, no totals).
        var expectedTotals = devicesToScan
            .Select(item => DeviceSnapshotReader.ReadManifestSourceObjects(item.Context.StagingRoot).Count)
            .ToArray();
        var exportProgress = progress is null ? null : new ExportProgressAggregator(progress, expectedTotals);
        var scanProgress = exportProgress is { HasTotals: true } ? exportProgress : progress;
        var scans = new Dictionary<string, DeviceScanResult>(StringComparer.Ordinal);
        foreach (var device in devicesToScan)
        {
            scanProgress?.Report($"Comparing TIA source for {device.Metadata.PlcName}...");
            var scan = await MeasureAsync(
                    timings,
                    "plc-source-scan",
                    "Produce a checksum-stable staged source snapshot for this PLC before comparing XML.",
                    device.Metadata.PlcName,
                    () => scanner.ScanAsync(
                        device.Context,
                        cancellationToken,
                        scanProgress,
                        device.Metadata.PlcName,
                        allowCompile,
                        forceFullExport),
                    result => $"Staged {result.Objects.Count} XML object(s); {result.UnsupportedObjects.Count} unsupported object(s).")
                .ConfigureAwait(false);
            scans[device.Metadata.DeviceId] = scan;
            if (scan.Timings is not null)
            {
                timings.AddRange(scan.Timings);
            }
            exportProgress?.DeviceCompleted();
        }

        var differences = new List<SourceDifference>();
        foreach (var device in devicesToScan)
        {
            var deviceDifferences = Measure(
                timings,
                "plc-xml-compare",
                "Normalize and compare master XML with the staged TIA XML snapshot.",
                device.Metadata.PlcName,
                () =>
                {
                    var masterObjects = new SourceTreeReader().Read(device.Context.SourceRoot);
                    var tiaObjects = scans[device.Metadata.DeviceId].Objects;
                    var masterMetadata = DeviceSnapshotReader.ReadManifestSourceObjects(device.Context.SourceRoot)
                        .ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
                    var tiaMetadata = DeviceSnapshotReader.ReadManifestSourceObjects(device.Context.StagingRoot)
                        .ToDictionary(item => item.RelativePath, StringComparer.Ordinal);
                    return CompareDevice(device.Metadata, device.Context, masterObjects, tiaObjects, masterMetadata, tiaMetadata)
                        .Concat(scans[device.Metadata.DeviceId].UnsupportedObjects.Select(unsupported =>
                            new SourceDifference(
                                device.Metadata.DeviceId,
                                device.Metadata.PlcName,
                                string.Empty,
                                unsupported.Name,
                                SourceDifferenceKind.Changed,
                                null,
                                null,
                                false)))
                        .ToArray();
                },
                result => DescribeDifferences(result));
            differences.AddRange(deviceDifferences);
        }

        var state = scans.Values.Any(scan => scan.UnsupportedObjects.Count > 0)
            ? ConsistencyState.ScanRequired
            : differences.Count == 0 && hardwareMatches && !safetyChanged
                ? safetyReadFailed
                    ? ConsistencyState.Unavailable
                    : ConsistencyState.Consistent
                : ConsistencyState.Different;
        return Persist(workbench, new WorkbenchConsistencyResult(
            Guid.NewGuid().ToString("N"),
            head.Sha,
            false,
            state,
            liveChecksums,
            differences,
            hardware,
            safety,
            safetyChanged,
            timings.ToArray(),
            HardwareChecked: includeHardware,
            ComparedWorktreeId: comparedWorktreeId));
    }

    /// <summary>Legacy fallback safety baseline per PLC name from master's revision.json, used
    /// only while a device's source manifest carries no safety data (workbenches created before
    /// the manifest baseline existed). Prefers the
    /// per-device <see cref="EngineeringSafetyState.Devices"/> entries (with per-block
    /// signatures); legacy files carry only the aggregate "Plc:fold;…" string, which is parsed
    /// without block detail.</summary>
    private static IReadOnlyDictionary<string, BaselineSafety> ReadBaselineSafety(string masterRoot)
    {
        var result = new Dictionary<string, BaselineSafety>(StringComparer.OrdinalIgnoreCase);
        var path = WorkbenchPaths.ResolveRevisionState(masterRoot);
        if (!File.Exists(path))
        {
            return result;
        }

        var state = EngineeringStateWriter.TryParse(File.ReadAllText(path))?.Safety;
        if (state is null)
        {
            return result;
        }

        if (state.Devices is not null)
        {
            foreach (var device in state.Devices)
            {
                result[device.PlcName] = new BaselineSafety(device.FSignature, device.BlockSignatures);
            }

            return result;
        }

        if (!string.IsNullOrWhiteSpace(state.FSignature))
        {
            foreach (var entry in state.FSignature.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split(':', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
                {
                    result[parts[0]] = new BaselineSafety(parts[1], null);
                }
            }
        }

        return result;
    }

    /// <summary>Diffs baseline vs live per-block signatures. Null when either side has no
    /// per-block record, so callers retain the aggregate-only legacy fallback.</summary>
    private static IReadOnlyList<SafetyBlockDifference>? DiffFBlockSignatures(
        IReadOnlyList<Contracts.Engineering.FBlockSignatureInfo>? baseline,
        IReadOnlyList<Contracts.Engineering.FBlockSignatureInfo>? live)
    {
        if (baseline is null || live is null)
        {
            return null;
        }

        var baselineByPath = baseline.ToDictionary(block => block.Path, block => block.Signature, StringComparer.Ordinal);
        var liveByPath = live.ToDictionary(block => block.Path, block => block.Signature, StringComparer.Ordinal);
        var changed = baselineByPath.Keys
            .Concat(liveByPath.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Where(path => !baselineByPath.TryGetValue(path, out var baselineSignature)
                || !liveByPath.TryGetValue(path, out var liveSignature)
                || !string.Equals(baselineSignature, liveSignature, StringComparison.Ordinal))
            .Select(path =>
            {
                baselineByPath.TryGetValue(path, out var baselineSignature);
                liveByPath.TryGetValue(path, out var liveSignature);
                if (baselineSignature is null)
                    return new SafetyBlockDifference(path, null, liveSignature, SafetyBlockDifferenceKind.Added);
                if (liveSignature is null)
                    return new SafetyBlockDifference(path, baselineSignature, null, SafetyBlockDifferenceKind.Removed);
                return new SafetyBlockDifference(
                    path,
                    baselineSignature,
                    liveSignature,
                    SafetyBlockDifferenceKind.Changed);
            })
            .ToArray();
        return changed;
    }

    private sealed record BaselineSafety(
        string? FSignature,
        IReadOnlyList<Contracts.Engineering.FBlockSignatureInfo>? BlockSignatures);

    private async Task<HardwareConfigurationCompareResult> CompareHardwareAsync(
        string masterRoot,
        CancellationToken cancellationToken,
        IOperationProgress? progress,
        ICollection<ComparisonTiming> timings)
    {
        var root = WorkbenchPaths.ResolveHardwareRoot(masterRoot);
        var stagingRoot = WorkbenchPaths.ResolveHardwareStagingRoot(masterRoot);
        TryDeleteDirectory(stagingRoot);
        Directory.CreateDirectory(stagingRoot);
        progress?.Report("Comparing project hardware configuration with TIA...");
        var liveResults = await engineering.CallAsync<HardwareExportResult[]>(
                "export_hardware_configuration",
                new { outputDir = stagingRoot, includeDeviceExports = false },
                cancellationToken)
            .ConfigureAwait(false);
        var warnings = HardwareConfigurationExport.EnsureSucceeded(liveResults, stagingRoot);
        var projectExport = liveResults.FirstOrDefault(result =>
            string.Equals(result.Scope, "project", StringComparison.OrdinalIgnoreCase));
        timings.Add(new ComparisonTiming(
            "hardware-aml-export",
            "Export the project CAx/AML artifact from TIA.",
            null,
            projectExport?.DurationMs ?? 0,
            projectExport is null
                ? "Project export result was not returned."
                : projectExport.Success
                    ? "Project CAx export completed."
                    : $"Project CAx export failed: {projectExport.Error ?? "unknown error"}."));
        timings.Add(new ComparisonTiming(
            "hardware-network-fingerprint",
            "Capture the project network-configuration fingerprint from TIA.",
            null,
            projectExport?.NetworkConfigurationDurationMs ?? 0,
            projectExport?.NetworkConfigurationDurationMs is null
                ? "Network timing was not supplied by the exporter."
                : "Network fingerprint capture completed; any capture warning is retained in the manifest."));

        var localComparison = Measure(
            timings,
            "hardware-local-compare",
            "Hash staged artifacts and compare them with the saved hardware baseline.",
            null,
            () =>
            {
                var local = HardwareConfigurationSnapshot.Read(root);
                var live = HardwareConfigurationSnapshot.FromResults(liveResults, stagingRoot);
                var artifacts = HardwareConfigurationSnapshot.Compare(local, live);
                return (Local: local, Artifacts: artifacts);
            },
            result => $"Compared {result.Artifacts.Count} hardware artifact(s).");
        var local = localComparison.Local;
        var artifacts = localComparison.Artifacts;
        var state = artifacts.All(artifact => artifact.State == "same")
            ? "in-sync"
            : local is null
                ? "missing"
                : "changed";
        var changed = artifacts.Count(artifact => artifact.State != "same");
        var message = state switch
        {
            "in-sync" => $"Project hardware configuration matches TIA ({artifacts.Count} artifact(s)).",
            "missing" => "No saved project-level hardware configuration exists yet. Review the staged TIA export before overwriting the baseline.",
            _ => $"Project hardware configuration differs from TIA ({changed} artifact(s) changed or missing). Review the staged TIA export before overwriting the baseline.",
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
    }

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

    public async Task<TiaSyncEvidence> ValidateSynchronizedMasterAsync(
        WorkbenchMetadata workbench,
        WorktreeMetadata master,
        string confirmedBy,
        CancellationToken cancellationToken = default,
        IOperationProgress? progress = null)
    {
        if (string.IsNullOrWhiteSpace(confirmedBy))
            throw new ArgumentException("A confirming Git identity is required.", nameof(confirmedBy));

        var masterRoot = ResolveMasterRoot(workbench, master);
        var pendingPath = Path.Combine(masterRoot, ".automation", WorkbenchWritePolicy.PendingFileName);
        var pending = store.TryRead<PendingMasterSynchronization>(pendingPath);
        if (pending is not null && pending.Sources.Count > 0)
            throw new WorkbenchLifecycleException(
                "MASTER_PENDING_SYNCHRONIZATION",
                "Commit or clear pending TIA synchronizations before creating exact validation evidence.");

        var status = await versionControl.CallAsync<ConsistencyStatusResult>(
                "vc_status",
                new { repoPath = masterRoot },
                cancellationToken)
            .ConfigureAwait(false);
        if (status.Entries.Any(entry => IsManagedSourceXml(entry.FilePath)))
            throw new WorkbenchLifecycleException(
                "MASTER_SOURCE_DIRTY",
                "The master source tree has local XML changes; commit them before creating TIA validation evidence.");

        var head = await ReadHeadAsync(masterRoot, cancellationToken).ConfigureAwait(false);
        var devices = LoadDevices(workbench, master);
        var evidenceDevices = new List<TiaSyncEvidenceDevice>(devices.Count);
        foreach (var device in devices)
        {
            progress?.Report($"Validating exact TIA source for {device.Metadata.PlcName}...");
            var scan = await scanner.ScanAsync(
                    device.Context,
                    cancellationToken,
                    progress,
                    device.Metadata.PlcName)
                .ConfigureAwait(false);
            if (scan.UnsupportedObjects.Count > 0)
                throw new WorkbenchLifecycleException(
                    "SOURCE_COVERAGE_INCOMPLETE",
                    $"TIA source coverage is incomplete for '{device.Metadata.PlcName}'.");

            var masterObjects = new SourceTreeReader().Read(device.Context.SourceRoot);
            var differences = CompareDevice(device.Metadata, device.Context, masterObjects, scan.Objects);
            if (differences.Count > 0)
                throw new WorkbenchLifecycleException(
                    "TIA_MASTER_NOT_EXACT",
                    $"TIA and master differ for '{device.Metadata.PlcName}' ({differences.Count} source object(s)).");

            evidenceDevices.Add(new TiaSyncEvidenceDevice
            {
                DeviceId = device.Metadata.DeviceId,
                PlcName = device.Metadata.PlcName,
                ProjectIdentity = scan.ProjectIdentity,
                ProjectChecksum = scan.ProjectChecksum,
                Objects = scan.Objects
                    .Select(item => new TiaSyncEvidenceObject
                    {
                        Identity = item.Identity,
                        RelativePath = $"{Path.GetRelativePath(device.Context.WorktreeRoot, device.Context.SourceRoot).Replace('\\', '/')}/{item.RelativePath}",
                        Sha256 = item.Sha256,
                    })
                    .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                    .ToArray(),
            });
        }

        // The exact XML scan above proves that the Git-managed source tree is equal to TIA.
        // Capture the complete lightweight evidence only after that proof, so the resulting v2
        // tag can be used as the next fingerprint-first compare baseline.
        foreach (var evidenceDevice in evidenceDevices)
        {
            var capture = await engineering.CallAsync<SourceEvidenceCaptureResult>(
                    "capture_source_evidence",
                    new { plcName = evidenceDevice.PlcName },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(capture.Snapshot.PlcName, evidenceDevice.PlcName, StringComparison.Ordinal))
            {
                throw new WorkbenchLifecycleException(
                    "SOURCE_EVIDENCE_PLC_MISMATCH",
                    $"TIA returned source evidence for '{capture.Snapshot.PlcName}' while validating '{evidenceDevice.PlcName}'.");
            }
            if (!capture.Snapshot.Checksum.IsCompiled)
            {
                throw new WorkbenchLifecycleException(
                    "SOURCE_EVIDENCE_UNCOMPILED",
                    $"TIA did not return a compiled software checksum while validating '{evidenceDevice.PlcName}'.");
            }

            evidenceDevice.ProjectChecksum = capture.Snapshot.Checksum.SoftwareChecksum!;
            evidenceDevice.SourceEvidence = capture.Snapshot.Objects ?? Array.Empty<ManagedSourceEvidenceObject>();
        }

        var currentHead = await ReadHeadAsync(masterRoot, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(head.Sha, currentHead.Sha, StringComparison.OrdinalIgnoreCase))
            throw new WorkbenchLifecycleException(
                "MASTER_HEAD_CHANGED",
                "Master changed during exact validation; run validation again.");

        var evidence = new TiaSyncEvidence
        {
            SchemaVersion = "2.0",
            EvidenceKind = "tia-managed-source",
            CommitSha = head.Sha,
            WorkbenchId = workbench.WorkbenchId,
            SourceWorktreeId = master.WorktreeId,
            ConfirmedAt = DateTimeOffset.UtcNow.ToString("O"),
            ConfirmedBy = confirmedBy,
            MachineValidated = false,
            ManagedSourceConsistent = true,
            Devices = evidenceDevices.OrderBy(device => device.DeviceId, StringComparer.Ordinal).ToArray(),
        };
        return await versionControl.CallAsync<TiaSyncEvidence>(
                "vc_validation_create",
                new { repoPath = masterRoot, evidence },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public WorkbenchConsistencyResult GetComparison(WorkbenchMetadata workbench, string comparisonId)
    {
        if (string.IsNullOrWhiteSpace(comparisonId)
            || comparisonId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || comparisonId is "." or "..")
            throw new ArgumentException("A valid comparison ID is required.", nameof(comparisonId));
        var path = Path.Combine(workbench.RootPath, ".automation", "comparisons", comparisonId + ".json");
        return store.Read<WorkbenchConsistencyResult>(path);
    }

    private WorkbenchConsistencyResult Persist(WorkbenchMetadata workbench, WorkbenchConsistencyResult result)
    {
        store.Write(Path.Combine(workbench.RootPath, ".automation", "comparisons", result.ComparisonId + ".json"), result);
        return result;
    }

    private async Task<ConsistencyCommit> ReadHeadAsync(string masterRoot, CancellationToken cancellationToken)
    {
        var log = await versionControl.CallAsync<ConsistencyLogResult>(
                "vc_log",
                new { repoPath = masterRoot, maxCount = 1 },
                cancellationToken)
            .ConfigureAwait(false);
        return log.Commits.FirstOrDefault()
            ?? throw new ReconciliationException("MASTER_HEAD_UNAVAILABLE", "The master worktree has no Git HEAD.");
    }

    private IReadOnlyList<(DeviceMetadata Metadata, DeviceContext Context)> LoadDevices(
        WorkbenchMetadata workbench,
        WorktreeMetadata master)
    {
        var result = new List<(DeviceMetadata, DeviceContext)>();
        foreach (var deviceId in master.DeviceIds)
        {
            var registration = workbench.Worktrees.Single(item => item.WorktreeId == master.WorktreeId);
            var devicesRoot = Path.Combine(workbench.RootPath, "worktrees", registration.RelativePath, "devices");
            var deviceDirectory = Directory.EnumerateDirectories(devicesRoot)
                .FirstOrDefault(path => store.TryRead<DeviceMetadata>(Path.Combine(path, "device.json"))?.DeviceId == deviceId)
                ?? throw new WorkbenchCatalogException("DEVICE_NOT_FOUND", $"Device '{deviceId}' was not found in master.");
            var metadata = store.TryRead<DeviceMetadata>(Path.Combine(deviceDirectory, "device.json"))
                ?? throw new WorkbenchCatalogException("DEVICE_NOT_FOUND", $"Device '{deviceId}' was not found in master.");
            var context = WorkbenchPaths.ResolveDevice(
                workbench.WorkbenchId,
                workbench.RootPath,
                master.WorktreeId,
                registration.RelativePath,
                deviceId,
                metadata.PlcName);
            result.Add((metadata, context));
        }
        return result;
    }

    private static string ResolveMasterRoot(WorkbenchMetadata workbench, WorktreeMetadata master)
    {
        var registration = workbench.Worktrees.Single(item => item.WorktreeId == master.WorktreeId);
        return WorkbenchPaths.ResolveWorktree(workbench.RootPath, registration.RelativePath);
    }

    private static bool ChecksumMatches(ConsistencyValidationEvidence evidence, DeviceMetadata metadata, string? live) =>
        string.Equals(evidence.Devices.FirstOrDefault(item => item.DeviceId == metadata.DeviceId)?.ProjectChecksum, live, StringComparison.Ordinal);

    private static IReadOnlyList<SourceDifference> CompareDevice(
        DeviceMetadata metadata,
        DeviceContext context,
        IReadOnlyList<SourceObjectSnapshot> master,
        IReadOnlyList<SourceObjectSnapshot> tia,
        IReadOnlyDictionary<string, SourceObjectInfo>? masterMetadata = null,
        IReadOnlyDictionary<string, SourceObjectInfo>? tiaMetadata = null)
    {
        var byPath = master.Concat(tia).Select(item => item.RelativePath).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal);
        return byPath
            .Select(path =>
            {
                var left = master.FirstOrDefault(item => item.RelativePath == path);
                var right = tia.FirstOrDefault(item => item.RelativePath == path);
                var kind = left is null ? SourceDifferenceKind.Added
                    : right is null ? SourceDifferenceKind.Deleted
                    : left.Sha256 == right.Sha256 ? SourceDifferenceKind.Unchanged
                    : SourceDifferenceKind.Changed;
                SourceObjectInfo? baselineMetadata = null;
                SourceObjectInfo? liveMetadata = null;
                masterMetadata?.TryGetValue(path, out baselineMetadata);
                tiaMetadata?.TryGetValue(path, out liveMetadata);
                var evidenceKind = EvidenceKindForCategory(right?.Category ?? left?.Category);
                return new SourceDifference(
                    metadata.DeviceId,
                    metadata.PlcName,
                    $"{Path.GetRelativePath(context.WorktreeRoot, context.SourceRoot).Replace('\\', '/')}/{path}",
                    right?.Identity ?? left?.Identity ?? path,
                    kind,
                    evidenceKind == ManagedSourceEvidenceKind.TagTable ? left?.ContentHash : left?.Sha256,
                    evidenceKind == ManagedSourceEvidenceKind.TagTable ? right?.ContentHash : right?.Sha256,
                    true,
                    FingerprintComparison.Compare(
                        baselineMetadata?.FingerprintComponents,
                        liveMetadata?.FingerprintComponents),
                    evidenceKind);
            })
            .Where(item => item.Kind != SourceDifferenceKind.Unchanged)
            .ToArray();
    }

    private async Task<WorkbenchConsistencyResult> CompareFingerprintFirstAsync(
        WorkbenchMetadata workbench,
        WorktreeMetadata master,
        ConsistencyCommit head,
        HardwareConfigurationCompareResult? hardware,
        bool hardwareChecked,
        ConsistencyValidationEvidence evidence,
        IReadOnlyList<(DeviceMetadata Metadata, DeviceContext Context)> devices,
        bool untrackableChange,
        ICollection<ComparisonTiming> timings,
        string? comparedWorktreeId,
        CancellationToken cancellationToken)
    {
        var differences = new List<SourceDifference>();
        var liveChecksums = new Dictionary<string, string?>(StringComparer.Ordinal);
        var safety = new List<DeviceSafetyEvidence>();
        var safetyReadFailed = false;
        var untrackable = untrackableChange;

        foreach (var device in devices)
        {
            var baselineDevice = evidence.Devices.Single(item =>
                string.Equals(item.DeviceId, device.Metadata.DeviceId, StringComparison.Ordinal));
            var candidateRoot = Path.Combine(
                device.Context.StagingRoot,
                // Keep the temporary capture path short enough for TIA Openness' legacy
                // MAX_PATH handling when the worktree and source-group names are long.
                ".fingerprint-candidates-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var baseline = new SourceEvidenceSnapshot
                {
                    PlcName = device.Metadata.PlcName,
                    Checksum = new PlcChecksumInfo
                    {
                        PlcName = device.Metadata.PlcName,
                        SoftwareChecksum = baselineDevice.ProjectChecksum,
                    },
                    Objects = baselineDevice.SourceEvidence!,
                };
                var capture = await MeasureAsync(
                    timings,
                    "plc-evidence-capture",
                    "Read every lightweight fingerprint, tag timestamp, and F-block signature under one TIA lock; export XML only for nominated candidates.",
                    device.Metadata.PlcName,
                    () => engineering.CallAsync<SourceEvidenceCaptureResult>(
                        "compare_source_evidence",
                        new { baseline, outputDir = candidateRoot, plcName = device.Metadata.PlcName },
                        cancellationToken),
                    result => $"Read {result.Snapshot.Objects.Count} evidence object(s); nominated {result.Candidates.Count} candidate(s), exported {result.CandidateExports.Count} XML file(s).")
                .ConfigureAwait(false);

            liveChecksums[device.Metadata.DeviceId] = capture.Snapshot.Checksum.SoftwareChecksum;
            untrackable |= capture.IsUntrackable;
            var exports = capture.CandidateExports.ToDictionary(item => item.Id, item => item, StringComparer.Ordinal);
            foreach (var candidate in capture.Candidates.Where(item => item.RequiresXmlExport))
            {
                if (!exports.TryGetValue(candidate.Id, out var export) || string.IsNullOrWhiteSpace(export.Export.Path))
                {
                    throw new WorkbenchLifecycleException(
                        "CANDIDATE_XML_MISSING",
                        $"Evidence candidate '{candidate.Id}' for '{device.Metadata.PlcName}' requires XML but the Engineering MCP result did not supply it.");
                }

                var difference = Measure(
                    timings,
                    "candidate-xml-compare",
                    "Normalize and compare one evidence-nominated XML object with its master counterpart.",
                    device.Metadata.PlcName,
                    () => CompareCandidateXml(device.Metadata, device.Context, candidateRoot, export.Export.Path!, candidate),
                    result => result is null ? "Candidate XML matches master." : $"{result.Kind} {result.Identity}.");
                if (difference is not null)
                {
                    differences.Add(difference);
                }
            }

            foreach (var candidate in capture.Candidates.Where(item =>
                         string.Equals(item.Reason, SourceEvidenceCandidateReason.Removed, StringComparison.Ordinal)))
            {
                var baselineObject = candidate.Baseline;
                if (baselineObject is null)
                {
                    continue;
                }
                var relativePath = ToRelativeXmlPath(baselineObject);
                var masterObject = new SourceTreeReader().TryReadRelative(device.Context.SourceRoot, relativePath);
                if (masterObject is not null)
                {
                    differences.Add(new SourceDifference(
                        device.Metadata.DeviceId,
                        device.Metadata.PlcName,
                        SourcePathForResult(device.Context, relativePath),
                        masterObject.Identity,
                        SourceDifferenceKind.Deleted,
                        masterObject.Sha256,
                        null,
                        true,
                        EvidenceKind: baselineObject.Kind));
                }
            }

            var fCandidates = capture.Candidates.Where(item => item.IsSafetyDifference).ToArray();
            var blockDifferences = fCandidates.Select(candidate => new SafetyBlockDifference(
                candidate.Live?.SourcePath ?? candidate.Baseline?.SourcePath ?? candidate.Id,
                candidate.Baseline?.FSignature,
                candidate.Live?.FSignature,
                string.Equals(candidate.Reason, SourceEvidenceCandidateReason.New, StringComparison.Ordinal)
                    ? SafetyBlockDifferenceKind.Added
                    : string.Equals(candidate.Reason, SourceEvidenceCandidateReason.Removed, StringComparison.Ordinal)
                        ? SafetyBlockDifferenceKind.Removed
                        : SafetyBlockDifferenceKind.Changed)).ToArray();
            var checksum = capture.Snapshot.Checksum;
            safety.Add(new DeviceSafetyEvidence(
                device.Metadata.DeviceId,
                device.Metadata.PlcName,
                checksum.IsSafetyDevice == true,
                checksum.FSignatureReadState,
                checksum.FSignature,
                null,
                fCandidates.Length > 0,
                checksum.FBlockSignatures,
                blockDifferences.Select(item => item.Path).ToArray(),
                blockDifferences));
            safetyReadFailed |= checksum.IsSafetyDevice == true
                    && string.Equals(checksum.FSignatureReadState, FSignatureReadState.ReadFailed, StringComparison.Ordinal);
            }
            finally
            {
                TryDeleteDirectory(candidateRoot);
            }
        }

        var safetyChanged = safety.Any(item => item.Changed);
        var hardwareMatches = !hardwareChecked || hardware?.State == "in-sync";
        var state = differences.Count == 0 && hardwareMatches && !safetyChanged
            ? safetyReadFailed
                ? ConsistencyState.Unavailable
                : ConsistencyState.Consistent
            : ConsistencyState.Different;
        return Persist(workbench, new WorkbenchConsistencyResult(
            Guid.NewGuid().ToString("N"),
            head.Sha,
            differences.Count == 0,
            state,
            liveChecksums,
            differences,
            hardware,
            safety,
            safetyChanged,
            timings.ToArray(),
            untrackable,
            HardwareChecked: hardwareChecked,
            ComparedWorktreeId: comparedWorktreeId));
    }

    private static bool HasChecksumEvidence(
        ConsistencyValidationEvidence? evidence,
        ConsistencyCommit head,
        IReadOnlyList<(DeviceMetadata Metadata, DeviceContext Context)> devices) =>
        evidence is not null
        && string.Equals(evidence.SchemaVersion, "2.0", StringComparison.Ordinal)
        && string.Equals(evidence.EvidenceKind, "tia-managed-source", StringComparison.Ordinal)
        && string.Equals(evidence.CommitSha, head.Sha, StringComparison.OrdinalIgnoreCase)
        && evidence.Devices.Length == devices.Count
        && devices.All(device => evidence.Devices.Any(candidate =>
            string.Equals(candidate.DeviceId, device.Metadata.DeviceId, StringComparison.Ordinal)
            && string.Equals(candidate.PlcName, device.Metadata.PlcName, StringComparison.Ordinal)
            && candidate.SourceEvidence is not null));

    private static SourceDifference? CompareCandidateXml(
        DeviceMetadata metadata,
        DeviceContext context,
        string candidateRoot,
        string candidatePath,
        SourceEvidenceCandidate candidate)
    {
        var relativePath = Path.GetRelativePath(candidateRoot, candidatePath).Replace('\\', '/');
        if (relativePath == ".." || relativePath.StartsWith("../", StringComparison.Ordinal))
        {
            throw new WorkbenchPathException($"Candidate XML '{candidatePath}' is outside its declared candidate root.");
        }

        var reader = new SourceTreeReader();
        var master = reader.TryReadRelative(context.SourceRoot, relativePath);
        var live = reader.TryReadRelative(candidateRoot, relativePath)
            ?? throw new WorkbenchLifecycleException("CANDIDATE_XML_NOT_FOUND", $"Candidate XML '{relativePath}' disappeared before comparison.");
        var evidenceKind = candidate.Live?.Kind ?? candidate.Baseline?.Kind;
        var kind = master is null
            ? SourceDifferenceKind.Added
            : string.Equals(master.Sha256, live.Sha256, StringComparison.Ordinal)
                ? SourceDifferenceKind.Unchanged
                : SourceDifferenceKind.Changed;
        return kind == SourceDifferenceKind.Unchanged
            ? null
            : new SourceDifference(
                metadata.DeviceId,
                metadata.PlcName,
                SourcePathForResult(context, relativePath),
                live.Identity,
                kind,
                string.Equals(evidenceKind, ManagedSourceEvidenceKind.TagTable, StringComparison.Ordinal) ? master?.ContentHash : master?.Sha256,
                string.Equals(evidenceKind, ManagedSourceEvidenceKind.TagTable, StringComparison.Ordinal) ? live.ContentHash : live.Sha256,
                true,
                FingerprintComparison.Compare(candidate.Baseline?.Fingerprints, candidate.Live?.Fingerprints),
                candidate.Live?.Kind ?? candidate.Baseline?.Kind);
    }

    private static string? EvidenceKindForCategory(string? category) =>
        string.Equals(category, "Tags", StringComparison.Ordinal)
            ? ManagedSourceEvidenceKind.TagTable
            : null;

    private static string ToRelativeXmlPath(ManagedSourceEvidenceObject source)
    {
        var folder = source.Category switch
        {
            "DB" => "DB",
            "Tags" => "Tags",
            "UDT" => "UDT",
            _ => "Blocks",
        };
        return $"{folder}/{source.SourcePath}.xml";
    }

    private static string SourcePathForResult(DeviceContext context, string relativePath) =>
        $"{Path.GetRelativePath(context.WorktreeRoot, context.SourceRoot).Replace('\\', '/')}/{relativePath}";

    private static async Task<T> MeasureAsync<T>(
        ICollection<ComparisonTiming> timings,
        string phase,
        string purpose,
        string? plcName,
        Func<Task<T>> action,
        Func<T, string> describe)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await action().ConfigureAwait(false);
        stopwatch.Stop();
        timings.Add(new ComparisonTiming(phase, purpose, plcName, stopwatch.ElapsedMilliseconds, describe(result)));
        return result;
    }

    private static T Measure<T>(
        ICollection<ComparisonTiming> timings,
        string phase,
        string purpose,
        string? plcName,
        Func<T> action,
        Func<T, string> describe)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = action();
        stopwatch.Stop();
        timings.Add(new ComparisonTiming(phase, purpose, plcName, stopwatch.ElapsedMilliseconds, describe(result)));
        return result;
    }

    private static string DescribeDifferences(IReadOnlyCollection<SourceDifference> differences)
    {
        if (differences.Count == 0)
        {
            return "No XML differences found.";
        }

        var examples = differences
            .Take(3)
            .Select(difference => $"{difference.Kind} {difference.Identity}");
        return $"Found {differences.Count} difference(s): {string.Join(", ", examples)}"
            + (differences.Count > 3 ? ", …" : string.Empty);
    }

    private static string DescribeHardwareOutcome(HardwareConfigurationCompareResult result)
    {
        var changed = result.Artifacts.Count(artifact => !string.Equals(artifact.State, "same", StringComparison.Ordinal));
        return $"Hardware state: {result.State}; {changed} artifact(s) differ.";
    }

    private static bool IsManagedSourceXml(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        return parts.Length >= 4
            && string.Equals(parts[0], "devices", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parts[2], "source", StringComparison.OrdinalIgnoreCase)
            && parts[^1].EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    }
}
