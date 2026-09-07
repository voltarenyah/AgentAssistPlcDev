namespace Contracts.Engineering;

/// <summary>Evidence categories participating in fingerprint-first managed-source comparison.</summary>
public static class ManagedSourceEvidenceKind
{
    public const string StandardBlock = "standard-block";
    public const string Udt = "udt";
    public const string TagTable = "tag-table";
    public const string FBlock = "f-block";

    /// <summary>Excluded from all managed-source evidence and comparison decisions.</summary>
    public const string InstanceDb = "instance-db";
}

/// <summary>Whether the selected lightweight evidence was read from TIA.</summary>
public static class ManagedSourceEvidenceReadState
{
    public const string Readable = "readable";
    public const string Unreadable = "unreadable";
}

/// <summary>One Git-managed PLC object flattened to its lightweight TIA evidence.</summary>
public sealed class ManagedSourceEvidenceObject
{
    /// <summary>Stable source identity shared by live capture and commit-bound evidence.</summary>
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ReadState { get; set; } = ManagedSourceEvidenceReadState.Readable;

    /// <summary>Named fingerprint values for standard blocks and UDTs; null means unreadable.</summary>
    public FingerprintSet? Fingerprints { get; set; }

    /// <summary>TIA <c>PlcTagTable.ModifiedTimeStamp</c>; null means unreadable for tag tables.</summary>
    public DateTimeOffset? ModifiedTimeStamp { get; set; }

    /// <summary>Per-F-block BlockOfflineSignature; null means unreadable. 00000000 is a valid
    /// value for a safety block that is not actually called.</summary>
    public string? FSignature { get; set; }
}

/// <summary>Stable reason strings for candidate selection and user-facing attribution.</summary>
public static class SourceEvidenceCandidateReason
{
    public const string New = "new";
    public const string Removed = "removed";
    public const string FingerprintChanged = "fingerprint-changed";
    public const string TagTimestampChanged = "tag-timestamp-changed";
    public const string EvidenceUnreadable = "evidence-unreadable";
    public const string FSignatureChanged = "f-signature-changed";
    public const string EvidenceKindChanged = "evidence-kind-changed";
}

/// <summary>One non-equal managed-source evidence observation.</summary>
public sealed class SourceEvidenceCandidate
{
    public string Id { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public bool RequiresXmlExport { get; set; }
    public bool IsSafetyDifference { get; set; }
    public ManagedSourceEvidenceObject? Baseline { get; set; }
    public ManagedSourceEvidenceObject? Live { get; set; }
}

/// <summary>Pure evidence comparison result, before any XML is read or normalized.</summary>
public sealed class SourceEvidenceComparisonResult
{
    public IReadOnlyList<SourceEvidenceCandidate> Candidates { get; set; } = Array.Empty<SourceEvidenceCandidate>();

    public bool IsUntrackable { get; set; }

    public IEnumerable<SourceEvidenceCandidate> XmlCandidates => Candidates.Where(candidate => candidate.RequiresXmlExport);
    public IEnumerable<SourceEvidenceCandidate> SafetyDifferences => Candidates.Where(candidate => candidate.IsSafetyDifference);
}

/// <summary>Full lightweight managed-source observation for one PLC at one instant.</summary>
public sealed class SourceEvidenceSnapshot
{
    public string PlcName { get; set; } = string.Empty;
    public PlcChecksumInfo Checksum { get; set; } = new();
    public IReadOnlyList<ManagedSourceEvidenceObject> Objects { get; set; } = Array.Empty<ManagedSourceEvidenceObject>();
}

/// <summary>One XML export produced for an evidence-nominated live object.</summary>
public sealed class SourceEvidenceCandidateExport
{
    public string Id { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public ExportResult Export { get; set; } = new();
}

/// <summary>
/// Result of an initial snapshot capture or a baseline comparison. Initial capture has no
/// candidates or candidate exports; compare only contains XML exports for nominated live objects.
/// </summary>
public sealed class SourceEvidenceCaptureResult
{
    public SourceEvidenceSnapshot Snapshot { get; set; } = new();
    public IReadOnlyList<SourceEvidenceCandidate> Candidates { get; set; } = Array.Empty<SourceEvidenceCandidate>();
    public IReadOnlyList<SourceEvidenceCandidateExport> CandidateExports { get; set; } = Array.Empty<SourceEvidenceCandidateExport>();
    public bool IsUntrackable { get; set; }
}
