using Contracts.Engineering;

namespace Agent.Workbench;

public enum ReconciliationChangeKind
{
    Added,
    Changed,
    Removed,
    Unchanged,
}

public sealed record ReconciliationEntry(
    string RelativePath,
    ReconciliationChangeKind Kind,
    string? BaselineHash,
    string? StagingHash,
    string? ComponentIdentity,
    string? StoredFingerprints = null,
    string? LiveFingerprints = null,
    bool? FingerprintsMatch = null,
    Dictionary<string, FingerprintComponentComparison>? FingerprintComponents = null);

public sealed record ReconciliationPreview(
    string PreviewId,
    string WorktreeId,
    string DeviceId,
    string BaselineTreeHash,
    string StagingTreeHash,
    IReadOnlyList<ReconciliationEntry> Entries);

public sealed record ReconciliationOutcome(
    string PreviewId,
    IReadOnlyList<string> ChangedPaths);

public sealed class ReconciliationException : Exception
{
    public ReconciliationException(string code, string message)
        : base($"{code}: {message}")
    {
        Code = code;
    }

    public ReconciliationException(string code, string message, Exception innerException)
        : base($"{code}: {message}", innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
