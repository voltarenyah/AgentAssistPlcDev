namespace Agent.Workbench;

public sealed record SourceObjectSnapshot(
    string Identity,
    string RelativePath,
    string Category,
    string Name,
    string Sha256,
    long Length);

public sealed record DeviceSourceSnapshot(
    string DeviceId,
    string PlcName,
    string ProjectIdentity,
    string ProjectChecksum,
    IReadOnlyList<SourceObjectSnapshot> Objects);

public enum ConsistencyState
{
    Consistent,
    Different,
    ScanRequired,
    Unavailable,
}

public enum SourceDifferenceKind
{
    Unchanged,
    Changed,
    Added,
    Deleted,
}

public sealed record SourceDifference(
    string DeviceId,
    string PlcName,
    string RelativePath,
    string Identity,
    SourceDifferenceKind Kind,
    string? MasterFingerprint,
    string? TiaFingerprint,
    bool Supported);

public sealed record WorkbenchConsistencyResult(
    string ComparisonId,
    string MasterSha,
    bool FastGatePassed,
    ConsistencyState State,
    IReadOnlyDictionary<string, string?> LiveChecksums,
    IReadOnlyList<SourceDifference> Differences,
    HardwareConfigurationCompareResult? Hardware = null);
