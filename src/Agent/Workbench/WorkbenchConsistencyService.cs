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
            return Persist(workbench, new WorkbenchConsistencyResult(
                Guid.NewGuid().ToString("N"),
                head.Sha,
                true,
                ConsistencyState.Consistent,
                liveChecksums,
                Array.Empty<SourceDifference>()));
        }

        var evidenceSourceCanNarrow = evidenceCurrent && sourceClean;
        var devicesToScan = evidenceSourceCanNarrow
            ? devices.Where(item => !ChecksumMatches(evidence!, item.Metadata, liveChecksums[item.Metadata.DeviceId])).ToArray()
            : devices.ToArray();
        var scans = new Dictionary<string, DeviceScanResult>(StringComparer.Ordinal);
        foreach (var device in devicesToScan)
        {
            progress?.Report($"Comparing TIA source for {device.Metadata.PlcName}...");
            scans[device.Metadata.DeviceId] = await scanner.ScanAsync(
                    device.Context,
                    cancellationToken,
                    progress,
                    device.Metadata.PlcName)
                .ConfigureAwait(false);
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
            : differences.Count == 0
                ? ConsistencyState.Consistent
                : ConsistencyState.Different;
        return Persist(workbench, new WorkbenchConsistencyResult(
            Guid.NewGuid().ToString("N"),
            head.Sha,
            false,
            state,
            liveChecksums,
            differences));
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
