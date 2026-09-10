using Contracts.Engineering;

namespace Agent.Workbench;

public sealed record SourceObjectSnapshot(
    string Identity,
    string RelativePath,
    string Category,
    string Name,
    string Sha256,
    long Length,
    string? ContentHash = null);

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

public enum SafetyBlockDifferenceKind
{
    Changed,
    Added,
    Removed,
}

public sealed record SafetyBlockDifference(
    string Path,
    string? BaselineSignature,
    string? CurrentSignature,
    SafetyBlockDifferenceKind Kind);

/// <summary>One observable phase of a TIA comparison, retained with the comparison result so a
/// diagnostic report can explain both elapsed time and the evidence produced by that phase.</summary>
public sealed record ComparisonTiming(
    string Phase,
    string Purpose,
    string? PlcName,
    long ElapsedMilliseconds,
    string Outcome);

public sealed record SourceDifference(
    string DeviceId,
    string PlcName,
    string RelativePath,
    string Identity,
    SourceDifferenceKind Kind,
    string? MasterFingerprint,
    string? TiaFingerprint,
    bool Supported,
    Dictionary<string, FingerprintComponentComparison>? FingerprintComponents = null,
    string? EvidenceKind = null);

/// <summary>Per-device safety evidence gathered during a compare: whether the PLC is a safety
/// device, the F-signature read state, the live offline collective F-signature, the baseline
/// recorded in master's revision.json, and whether the signature changed. Changed covers a live
/// signature that differs from the baseline and a lost safety surface (baseline signature with
/// no live safety device). A baseline signature with no live signature on a still-present safety
/// device (e.g. the safety program was never compiled on the comparing machine's session) is
/// degraded evidence and surfaces as Unavailable, not as a phantom change.
/// <see cref="ChangedBlocks"/> attributes the change to individual F-blocks when both sides
/// recorded per-block signatures (additive 2026-09-02; null when either side lacks them).</summary>
public sealed record DeviceSafetyEvidence(
    string DeviceId,
    string PlcName,
    bool IsSafetyDevice,
    string? ReadState,
    string? FSignature,
    string? BaselineFSignature,
    bool Changed,
    IReadOnlyList<Contracts.Engineering.FBlockSignatureInfo>? FBlockSignatures = null,
    IReadOnlyList<string>? ChangedBlocks = null,
    IReadOnlyList<SafetyBlockDifference>? BlockDifferences = null);

public sealed record WorkbenchConsistencyResult(
    string ComparisonId,
    string MasterSha,
    bool FastGatePassed,
    ConsistencyState State,
    IReadOnlyDictionary<string, string?> LiveChecksums,
    IReadOnlyList<SourceDifference> Differences,
    HardwareConfigurationCompareResult? Hardware = null,
    IReadOnlyList<DeviceSafetyEvidence>? Safety = null,
    bool SafetyChanged = false,
    IReadOnlyList<ComparisonTiming>? Timings = null,
    bool UntrackableChange = false,
    bool HardwareChecked = true,
    /// <summary>The worktree whose registered TIA project supplied the live comparison.</summary>
    string? ComparedWorktreeId = null);
