namespace Contracts.Engineering;

/// <summary>sync_export entry per PLC export root (buildnote/plan/export-sync.md).</summary>
public sealed class SyncResult
{
    public string PlcName { get; set; } = string.Empty;
    public string ExportRoot { get; set; } = string.Empty;

    /// <summary>"unchanged" (checksum gate held, nothing exported) or "updated" (diff ran).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>PlcSoftwareChecksum stored in the manifest before this run; null when absent/legacy.</summary>
    public string? ChecksumBefore { get; set; }

    /// <summary>Checksum read from TIA during this run; null when unsupported or not compiled.</summary>
    public string? ChecksumAfter { get; set; }

    /// <summary>In TIA but not in the manifest → exported for the first time.</summary>
    public SyncChange[] Added { get; set; } = Array.Empty<SyncChange>();

    /// <summary>Timestamp-nominated and hash-confirmed content changes.</summary>
    public SyncChange[] Changed { get; set; } = Array.Empty<SyncChange>();

    /// <summary>No real content change: timestamp moved but the XML hashes identically, or a
    /// legacy record got its fingerprints backfilled without export.</summary>
    public SyncChange[] Touched { get; set; } = Array.Empty<SyncChange>();

    /// <summary>In the manifest but gone from TIA → XML deleted, record dropped.</summary>
    public SyncChange[] Removed { get; set; } = Array.Empty<SyncChange>();

    /// <summary>Export attempted and failed (record keeps Failed status in the manifest).</summary>
    public SyncChange[] Failed { get; set; } = Array.Empty<SyncChange>();
}

public sealed class SyncChange
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string? ExportedFile { get; set; }

    /// <summary>Why the planner nominated this component (e.g. "new", "timestamp", "legacy-no-hash", "removed-from-tia", "unreadable-metadata").</summary>
    public string? Reason { get; set; }
}
