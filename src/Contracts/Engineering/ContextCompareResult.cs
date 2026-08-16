namespace Contracts.Engineering;

/// <summary>compare_context entry per PLC export root (buildnote/plan/export-sync.md §Compare):
/// a read-only per-component diff between the live project and the stored manifest — the data
/// behind the App's Compare tab. No exports, no writes.</summary>
public sealed class ContextCompareResult
{
    public string PlcName { get; set; } = string.Empty;
    public string ExportRoot { get; set; } = string.Empty;
    public bool ManifestExists { get; set; }
    public string? StoredChecksum { get; set; }
    public string? LiveChecksum { get; set; }
    public ContextCompareEntry[] Components { get; set; } = Array.Empty<ContextCompareEntry>();
}

public sealed class ContextCompareEntry
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Canonical live fingerprint string; null for tag tables / unreadable providers.</summary>
    public string? LiveFingerprints { get; set; }

    /// <summary>Fingerprint string stored in the manifest; null on legacy records / tag tables.</summary>
    public string? StoredFingerprints { get; set; }

    /// <summary>Null when either side has no fingerprints; otherwise the exact comparison.</summary>
    public bool? FingerprintsMatch { get; set; }

    /// <summary>Per-fingerprint evidence keyed by TIA fingerprint id. Hashes are retained for
    /// hover/detail views while the normal comparison surface can show only same/different.</summary>
    public Dictionary<string, FingerprintComponentComparison>? FingerprintComponents { get; set; }

    public DateTimeOffset? LiveModifiedDate { get; set; }
    public DateTimeOffset? StoredModifiedDate { get; set; }

    /// <summary>"same" | "different" | "new" (live only) | "missing" (manifest only) |
    /// "unverifiable" (instance DB — only a sync's hash check can tell) | "unknown"
    /// (detection signal insufficient — e.g. legacy hash-less record or unreadable metadata).</summary>
    public string State { get; set; } = string.Empty;
}
