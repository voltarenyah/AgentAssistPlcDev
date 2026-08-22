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
        IOperationProgress? progress = null)
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
        var checksums = await engineering.CallAsync<PlcChecksumInfo[]>(
                "get_plc_checksums",
                new { },
                cancellationToken)
            .ConfigureAwait(false);
        var devices = LoadDevices(workbench, master);
        var liveChecksums = devices.ToDictionary(
            item => item.Metadata.DeviceId,
            item => checksums.FirstOrDefault(checksum =>
                string.Equals(checksum.PlcName, item.Metadata.PlcName, StringComparison.OrdinalIgnoreCase))?.SoftwareChecksum,
            StringComparer.Ordinal);

        var sourceClean = !status.Entries.Any(entry => IsManagedSourceXml(entry.FilePath));

        // The checksum is the comparison currency: without it the TIA project has
        // uncompiled/unsaved changes and the fingerprint scan would fail anyway
        // (PlcSourceScanner requires it). The caller offers compile+save and retries.
        var uncompiled = devices
            .Where(item => string.IsNullOrEmpty(liveChecksums[item.Metadata.DeviceId]))
            .Select(item => item.Metadata.PlcName)
            .ToArray();
        if (uncompiled.Length > 0)
        {
            throw new WorkbenchLifecycleException(
                "PLC_COMPILE_REQUIRED",
                $"No software checksum for {string.Join(", ", uncompiled)} — the TIA project was changed but not compiled and saved. Compile and save the TIA project, then run the comparison again.");
        }

        // Baseline for "TIA software is unchanged since ...": exact HEAD validation evidence
        // first, then the checksums HEAD itself recorded in revision.json (savepoints and the
        // workbench baseline commit compile TIA and record them). Any plain source commit in
        // between invalidates the baseline and forces the fingerprint scan.
        var baseline = BuildEvidenceBaseline(evidence, head, devices)
            ?? await ReadRecordedBaselineAsync(masterRoot, head.Sha, cancellationToken).ConfigureAwait(false);

        var matchesBaseline = baseline is not null
            && sourceClean
            && devices.All(item => MatchesBaseline(baseline, item.Metadata, liveChecksums[item.Metadata.DeviceId]));
        if (matchesBaseline)
        {
            return Persist(workbench, new WorkbenchConsistencyResult(
                Guid.NewGuid().ToString("N"),
                head.Sha,
                true,
                hardware.State == "in-sync" ? ConsistencyState.Consistent : ConsistencyState.Different,
                liveChecksums,
                Array.Empty<SourceDifference>(),
                hardware));
        }

        var canNarrow = baseline is not null && sourceClean;
        var devicesToScan = canNarrow
            ? devices.Where(item => !MatchesBaseline(baseline!, item.Metadata, liveChecksums[item.Metadata.DeviceId])).ToArray()
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
                    device.Metadata.PlcName)
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
            : differences.Count == 0 && hardware.State == "in-sync"
                ? ConsistencyState.Consistent
                : ConsistencyState.Different;
        return Persist(workbench, new WorkbenchConsistencyResult(
            Guid.NewGuid().ToString("N"),
            head.Sha,
            false,
            state,
            liveChecksums,
            differences,
            hardware));
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

    /// <summary>
    /// Best-effort evidence stamping after a master commit: when the commit completes the newest
    /// comparison's difference set and TIA still holds the compared checksums, the commit is
    /// stamped with tia-sync evidence — the software checksum is the proof. Partial commits only
    /// advance the comparison's committed-path ledger; a later completing commit still earns the
    /// evidence. A failed stamp must never fail the commit itself.
    /// </summary>
    public async Task TryStampSynchronizedCommitAsync(
        WorkbenchMetadata workbench,
        WorktreeMetadata master,
        string commitSha,
        IReadOnlyList<string> committedPaths,
        string confirmedBy,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await StampSynchronizedCommitAsync(workbench, master, commitSha, committedPaths, confirmedBy, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best-effort: evidence must never fail or roll back a completed commit.
        }
    }

    private async Task StampSynchronizedCommitAsync(
        WorkbenchMetadata workbench,
        WorktreeMetadata master,
        string commitSha,
        IReadOnlyList<string> committedPaths,
        string confirmedBy,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(workbench.RootPath, ".automation", "comparisons");
        if (!Directory.Exists(directory))
        {
            return;
        }

        var latest = Directory.EnumerateFiles(directory, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (latest is null)
        {
            return;
        }

        var comparison = store.TryRead<WorkbenchConsistencyResult>(latest);
        if (comparison is null || comparison.Differences.Any(difference => !difference.Supported))
        {
            // Unsupported objects mean incomplete export coverage — committing the listed items
            // can never prove the whole software state, so no evidence is possible.
            return;
        }

        var required = comparison.Differences
            .Select(difference => difference.RelativePath)
            .Where(path => !string.IsNullOrEmpty(path))
            .ToHashSet(StringComparer.Ordinal);
        if (required.Count == 0)
        {
            return;
        }

        var covered = (comparison.CommittedPaths ?? Array.Empty<string>())
            .Concat(committedPaths)
            .ToHashSet(StringComparer.Ordinal);
        var ledger = covered.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        if (!required.IsSubsetOf(covered))
        {
            Persist(workbench, comparison with { CommittedPaths = ledger });
            return;
        }

        var masterRoot = ResolveMasterRoot(workbench, master);

        // The source tree must be fully committed — leftover dirty XML means master still differs.
        var status = await versionControl.CallAsync<ConsistencyStatusResult>(
                "vc_status",
                new { repoPath = masterRoot },
                cancellationToken)
            .ConfigureAwait(false);
        if (status.Entries.Any(entry => IsManagedSourceXml(entry.FilePath)))
        {
            return;
        }

        // TIA must still hold the compared checksums, otherwise the diff list is stale.
        var devices = LoadDevices(workbench, master);
        var checksums = await engineering.CallAsync<PlcChecksumInfo[]>(
                "get_plc_checksums",
                new { },
                cancellationToken)
            .ConfigureAwait(false);
        var liveByPlc = checksums
            .Where(checksum => !string.IsNullOrEmpty(checksum.SoftwareChecksum))
            .ToDictionary(checksum => checksum.PlcName, StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            var expected = comparison.LiveChecksums.TryGetValue(device.Metadata.DeviceId, out var value) ? value : null;
            if (string.IsNullOrEmpty(expected)
                || !liveByPlc.TryGetValue(device.Metadata.PlcName, out var live)
                || !string.Equals(expected, live.SoftwareChecksum, StringComparison.Ordinal))
            {
                return;
            }
        }

        // Content proof: every compared TIA fingerprint must now be master's committed content
        // (a null fingerprint means "absent in TIA" — the local file must be gone).
        foreach (var device in devices)
        {
            var prefix = $"{Path.GetRelativePath(device.Context.WorktreeRoot, device.Context.SourceRoot).Replace('\\', '/')}/";
            var current = new SourceTreeReader().Read(device.Context.SourceRoot)
                .ToDictionary(item => prefix + item.RelativePath, item => item.Sha256, StringComparer.Ordinal);
            foreach (var difference in comparison.Differences.Where(item => item.DeviceId == device.Metadata.DeviceId))
            {
                var matches = difference.TiaFingerprint is null
                    ? !current.ContainsKey(difference.RelativePath)
                    : current.TryGetValue(difference.RelativePath, out var sha)
                        && string.Equals(sha, difference.TiaFingerprint, StringComparison.Ordinal);
                if (!matches)
                {
                    return;
                }
            }
        }

        var evidence = new TiaSyncEvidence
        {
            SchemaVersion = "1.0",
            EvidenceKind = "tia-sync",
            CommitSha = commitSha,
            WorkbenchId = workbench.WorkbenchId,
            SourceWorktreeId = master.WorktreeId,
            ConfirmedAt = DateTimeOffset.UtcNow.ToString("O"),
            ConfirmedBy = confirmedBy,
            MachineValidated = false,
            Devices = devices.Select(device => new TiaSyncEvidenceDevice
            {
                DeviceId = device.Metadata.DeviceId,
                PlcName = device.Metadata.PlcName,
                ProjectIdentity = liveByPlc[device.Metadata.PlcName].ProjectIdentity,
                ProjectChecksum = liveByPlc[device.Metadata.PlcName].SoftwareChecksum!,
                Objects = comparison.Differences
                    .Where(difference => difference.DeviceId == device.Metadata.DeviceId && difference.TiaFingerprint is not null)
                    .Select(difference => new TiaSyncEvidenceObject
                    {
                        Identity = difference.Identity,
                        RelativePath = difference.RelativePath,
                        Sha256 = difference.TiaFingerprint!,
                    })
                    .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
                    .ToArray(),
            }).OrderBy(device => device.DeviceId, StringComparer.Ordinal).ToArray(),
        };
        await versionControl.CallAsync<TiaSyncEvidence>(
                "vc_validation_create",
                new { repoPath = masterRoot, evidence },
                cancellationToken)
            .ConfigureAwait(false);
        Persist(workbench, comparison with { CommittedPaths = ledger });
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

    private static Dictionary<string, string>? BuildEvidenceBaseline(
        ConsistencyValidationEvidence? evidence,
        ConsistencyCommit head,
        IReadOnlyList<(DeviceMetadata Metadata, DeviceContext Context)> devices)
    {
        if (evidence is null
            || !string.Equals(evidence.CommitSha, head.Sha, StringComparison.OrdinalIgnoreCase)
            || evidence.Devices.Length != devices.Count)
        {
            return null;
        }

        var baseline = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var device in evidence.Devices)
        {
            baseline[device.PlcName] = device.ProjectChecksum;
        }

        return baseline;
    }

    /// <summary>Checksums recorded by HEAD's own revision.json (savepoints and the workbench
    /// baseline commit compile TIA and record the aggregate). Null when HEAD recorded none —
    /// a later plain source commit means the recorded state no longer describes the tree.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> ReadRecordedBaselineAsync(
        string masterRoot,
        string headSha,
        CancellationToken cancellationToken)
    {
        var recordLog = await versionControl.CallAsync<ConsistencyLogResult>(
                "vc_log",
                new { repoPath = masterRoot, maxCount = 1, filePath = EngineeringStateWriter.RelativePath },
                cancellationToken)
            .ConfigureAwait(false);
        var recordSha = recordLog.Commits.FirstOrDefault()?.Sha;
        if (recordSha is null || !string.Equals(recordSha, headSha, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = WorkbenchPaths.ResolveRevisionState(masterRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        EngineeringRevisionState state;
        try
        {
            state = EngineeringStateWriter.Read(path);
        }
        catch (WorkbenchLifecycleException)
        {
            return null;
        }

        return ParseAggregateChecksum(state.Tia?.ProjectChecksum);
    }

    private static bool MatchesBaseline(
        IReadOnlyDictionary<string, string> baseline,
        DeviceMetadata metadata,
        string? live) =>
        live is not null
        && baseline.TryGetValue(metadata.PlcName, out var expected)
        && string.Equals(expected, live, StringComparison.Ordinal);

    /// <summary>Parses the aggregate "PLC_1:AA BB;PLC_2:CC DD" written by AggregateProjectChecksum.</summary>
    private static Dictionary<string, string>? ParseAggregateChecksum(string? aggregate)
    {
        if (string.IsNullOrWhiteSpace(aggregate))
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in aggregate.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf(':');
            if (separator <= 0)
            {
                return null;
            }

            map[part[..separator]] = part[(separator + 1)..];
        }

        return map.Count == 0 ? null : map;
    }

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
