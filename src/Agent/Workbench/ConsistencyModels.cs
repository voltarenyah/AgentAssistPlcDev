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

/// <summary>Per-device safety evidence gathered during a compare: whether the PLC is a safety
/// device, the F-signature read state, the live offline collective F-signature, the baseline
/// recorded in master's revision.json, and whether the signature changed (null-safe: appearing,
/// disappearing, or an applicability change all count as changed).</summary>
public sealed record DeviceSafetyEvidence(
    string DeviceId,
    string PlcName,
    bool IsSafetyDevice,
    string? ReadState,
    string? FSignature,
    string? BaselineFSignature,
    bool Changed);

public sealed record WorkbenchConsistencyResult(
    string ComparisonId,
    string MasterSha,
    bool FastGatePassed,
    ConsistencyState State,
    IReadOnlyDictionary<string, string?> LiveChecksums,
    IReadOnlyList<SourceDifference> Differences,
    HardwareConfigurationCompareResult? Hardware = null,
    IReadOnlyList<DeviceSafetyEvidence>? Safety = null,
    bool SafetyChanged = false);
