using Agent.Mcp;
using Contracts.Engineering;

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
    public string CommitSha { get; set; } = string.Empty;
    public ConsistencyValidationDevice[] Devices { get; set; } = Array.Empty<ConsistencyValidationDevice>();
}

public sealed class ConsistencyValidationDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public string PlcName { get; set; } = string.Empty;
    public string ProjectChecksum { get; set; } = string.Empty;
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
    public IReadOnlyList<TiaSyncEvidenceDevice> Devices { get; set; } = Array.Empty<TiaSyncEvidenceDevice>();
}

public sealed class TiaSyncEvidenceDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public string PlcName { get; set; } = string.Empty;
    public string ProjectIdentity { get; set; } = string.Empty;
    public string ProjectChecksum { get; set; } = string.Empty;
    public IReadOnlyList<TiaSyncEvidenceObject> Objects { get; set; } = Array.Empty<TiaSyncEvidenceObject>();
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
        bool forceFullExport = false)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        ArgumentNullException.ThrowIfNull(master);
        if (!string.Equals(workbench.WorkbenchId, master.WorkbenchId, StringComparison.Ordinal))
            throw new WorkbenchCatalogException("WORKBENCH_RELATIONSHIP_MISMATCH", "The master worktree belongs to another workbench.");

        var masterRoot = ResolveMasterRoot(workbench, master);
        var head = await ReadHeadAsync(masterRoot, cancellationToken).ConfigureAwait(false);
        var hardware = await CompareHardwareAsync(masterRoot, cancellationToken, progress).ConfigureAwait(false);
        var evidence = await versionControl.CallAsync<ConsistencyValidationEvidence?>(
                "vc_validation_get",
                new { repoPath = masterRoot, commitSha = head.Sha },
                cancellationToken)
            .ConfigureAwait(false);
        var status = await versionControl.CallAsync<ConsistencyStatusResult>(
                "vc_status",
                new { repoPath = masterRoot },
                cancellationToken)
            .ConfigureAwait(false);
        var devices = LoadDevices(workbench, master);
        if (allowCompile)
        {
            await engineering.CallAsync<object>("save_project", new { }, cancellationToken).ConfigureAwait(false);
            foreach (var device in devices)
            {
                var compile = await engineering.CallAsync<CompileResult>(
                        "compile_plc",
                        new { plcName = device.Metadata.PlcName },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (string.Equals(compile.State, "error", StringComparison.OrdinalIgnoreCase))
                    throw new WorkbenchLifecycleException("PLC_COMPILE_FAILED", $"Automatic PLC compile failed for '{device.Metadata.PlcName}'.");
            }
        }
        var checksums = await engineering.CallAsync<PlcChecksumInfo[]>(
                "get_plc_checksums",
                new { },
                cancellationToken)
            .ConfigureAwait(false);
        var liveChecksums = devices.ToDictionary(
            item => item.Metadata.DeviceId,
            item => checksums.FirstOrDefault(checksum =>
                string.Equals(checksum.PlcName, item.Metadata.PlcName, StringComparison.OrdinalIgnoreCase))?.SoftwareChecksum,
            StringComparer.Ordinal);

        // Safety evidence: the offline collective F-signature is read for every PLC on every
        // compare (get_plc_checksums above) and checked against master's revision.json. A changed
        // signature must make the result non-consistent even when checksums and XML are unchanged;
        // a failed required read must never pass as consistent. Three distinct situations:
        // - live signature present and different (or newly appearing) -> Changed.
        // - baseline signature present but the live device no longer reports a safety surface
        //   (F-CPU replaced by a standard CPU / safety program deleted) -> Changed.
        // - baseline signature present, live still a safety device, but no live signature
        //   (the SafetySignatureProvider is license-gated: no STEP 7 Safety license on the
        //   comparing machine, verified 2026-09-01) -> degraded evidence, Unavailable below,
        //   never a phantom change.
        var baselineFSignatures = ReadBaselineFSignatures(masterRoot);
        var safety = devices.Select(item =>
            {
                var live = checksums.FirstOrDefault(checksum =>
                    string.Equals(checksum.PlcName, item.Metadata.PlcName, StringComparison.OrdinalIgnoreCase));
                baselineFSignatures.TryGetValue(item.Metadata.PlcName, out var baselineFSignature);
                return new DeviceSafetyEvidence(
                    item.Metadata.DeviceId,
                    item.Metadata.PlcName,
                    live?.IsSafetyDevice == true,
                    live?.FSignatureReadState,
                    live?.FSignature,
                    baselineFSignature,
                    Changed: (live?.FSignature is not null
                            && !string.Equals(live.FSignature, baselineFSignature ?? string.Empty, StringComparison.Ordinal))
                        || (live?.IsSafetyDevice != true && baselineFSignature is not null));
            })
            .ToArray();
        var safetyChanged = safety.Any(item => item.Changed);
        var safetyReadFailed = safety.Any(item =>
            item.IsSafetyDevice
            && (string.Equals(item.ReadState, FSignatureReadState.ReadFailed, StringComparison.Ordinal)
                || (item.BaselineFSignature is not null && item.FSignature is null)));

        var sourceClean = !status.Entries.Any(entry => IsManagedSourceXml(entry.FilePath));
        var evidenceCurrent = evidence is not null
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
            var fastState = safetyChanged || hardware.State != "in-sync"
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
                safetyChanged));
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
            scans[device.Metadata.DeviceId] = await scanner.ScanAsync(
                    device.Context,
                    cancellationToken,
                    scanProgress,
                    device.Metadata.PlcName,
                    allowCompile,
                    forceFullExport)
                .ConfigureAwait(false);
            exportProgress?.DeviceCompleted();
        }

        var differences = new List<SourceDifference>();
        foreach (var device in devicesToScan)
        {
            var masterObjects = new SourceTreeReader().Read(device.Context.SourceRoot);
            var tiaObjects = scans[device.Metadata.DeviceId].Objects;
            differences.AddRange(CompareDevice(device.Metadata, device.Context, masterObjects, tiaObjects));
            differences.AddRange(scans[device.Metadata.DeviceId].UnsupportedObjects.Select(unsupported =>
                new SourceDifference(
                    device.Metadata.DeviceId,
                    device.Metadata.PlcName,
                    string.Empty,
                    unsupported.Name,
                    SourceDifferenceKind.Changed,
                    null,
                    null,
                    false)));
        }

        var state = scans.Values.Any(scan => scan.UnsupportedObjects.Count > 0)
            ? ConsistencyState.ScanRequired
            : differences.Count == 0 && hardware.State == "in-sync" && !safetyChanged
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
            safetyChanged));
    }

    /// <summary>Per-PLC baseline F-signatures from master's revision.json ("PLC:SIG;PLC2:SIG2").
    /// Missing/legacy revision state yields an empty map — a signature appearing since then then
    /// correctly reads as a change.</summary>
    private static IReadOnlyDictionary<string, string> ReadBaselineFSignatures(string masterRoot)
    {
        var path = WorkbenchPaths.ResolveRevisionState(masterRoot);
        var aggregate = File.Exists(path)
            ? EngineeringStateWriter.TryParse(File.ReadAllText(path))?.Safety?.FSignature
            : null;
        if (string.IsNullOrWhiteSpace(aggregate))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in aggregate.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            {
                result[parts[0]] = parts[1];
            }
        }

        return result;
    }

    private async Task<HardwareConfigurationCompareResult> CompareHardwareAsync(
        string masterRoot,
        CancellationToken cancellationToken,
        IOperationProgress? progress)
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
        var local = HardwareConfigurationSnapshot.Read(root);
        var live = HardwareConfigurationSnapshot.FromResults(liveResults, stagingRoot);
        var artifacts = HardwareConfigurationSnapshot.Compare(local, live);
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

        var currentHead = await ReadHeadAsync(masterRoot, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(head.Sha, currentHead.Sha, StringComparison.OrdinalIgnoreCase))
            throw new WorkbenchLifecycleException(
                "MASTER_HEAD_CHANGED",
                "Master changed during exact validation; run validation again.");

        var evidence = new TiaSyncEvidence
        {
            SchemaVersion = "1.0",
            EvidenceKind = "tia-sync",
            CommitSha = head.Sha,
            WorkbenchId = workbench.WorkbenchId,
            SourceWorktreeId = master.WorktreeId,
            ConfirmedAt = DateTimeOffset.UtcNow.ToString("O"),
            ConfirmedBy = confirmedBy,
            MachineValidated = false,
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
        IReadOnlyList<SourceObjectSnapshot> tia)
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
                return new SourceDifference(
                    metadata.DeviceId,
                    metadata.PlcName,
                    $"{Path.GetRelativePath(context.WorktreeRoot, context.SourceRoot).Replace('\\', '/')}/{path}",
                    right?.Identity ?? left?.Identity ?? path,
                    kind,
                    left?.Sha256,
                    right?.Sha256,
                    true);
            })
            .Where(item => item.Kind != SourceDifferenceKind.Unchanged)
            .ToArray();
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
