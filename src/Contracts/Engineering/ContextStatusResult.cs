namespace Contracts.Engineering;

/// <summary>get_context_status entry per PLC export root (buildnote/plan/export-sync.md §UI).
/// Pure read: the stored manifest checksum vs the live PLC software checksum — no exports,
/// no writes. Lets a caller show "new project / in-sync / changed" before deciding to sync.</summary>
public sealed class ContextStatusResult
{
    public string PlcName { get; set; } = string.Empty;
    public string ExportRoot { get; set; } = string.Empty;

    /// <summary>False when no metadata.json exists at the export root (new project / no context data).</summary>
    public bool ManifestExists { get; set; }

    /// <summary>Component records in the manifest (0 when absent).</summary>
    public int ComponentCount { get; set; }

    /// <summary>plcSoftwareChecksum stored in the manifest; null when absent or legacy.</summary>
    public string? StoredChecksum { get; set; }

    /// <summary>Checksum read from TIA right now; null when unsupported or not compiled.</summary>
    public string? LiveChecksum { get; set; }

    /// <summary>True when the PLC exposes the safety surface; null when read by an older adapter.</summary>
    public bool? IsSafetyDevice { get; set; }

    /// <summary>One of FSignatureReadState; null for non-safety devices.</summary>
    public string? FSignatureReadState { get; set; }

    /// <summary>Live offline collective F-signature (folded per-block, spaced hex); null when
    /// unavailable.</summary>
    public string? FSignature { get; set; }

    /// <summary>Per-F-block signatures behind <see cref="FSignature"/>; null when unavailable.</summary>
    public IReadOnlyList<FBlockSignatureInfo>? FBlockSignatures { get; set; }

    /// <summary>"no-baseline" | "in-sync" | "changed" | "unknown" (live checksum unavailable, or
    /// legacy manifest without a stored checksum — only a sync's detailed diff can decide).</summary>
    public string State { get; set; } = string.Empty;
}
