namespace Contracts.Engineering;

/// <summary>Read state of the offline collective F-signature (SafetySignatureProvider).
/// Serialized into commit-state tags and revision.json; values are stable strings.</summary>
public static class FSignatureReadState
{
    /// <summary>Safety device; the offline collective F-signature was read.</summary>
    public const string Ok = "ok";

    /// <summary>Safety device, but no signature is available (e.g. safety program never
    /// compiled, so no F-block exposes a BlockOfflineSignature).</summary>
    public const string NoSignature = "no-signature";

    /// <summary>Safety device whose signature read failed — never treat as an ordinary PLC.</summary>
    public const string ReadFailed = "read-failed";
}

/// <summary>One F-block's offline signature (SafetySignatureProvider on the block, TIA Openness
/// manual §5.27.4). A pair list rather than a map so block paths survive serializers that
/// camelCase dictionary keys. "00000000" means the block's signature is missing or invalidated
/// by an uncompiled change.</summary>
public sealed class FBlockSignatureInfo
{
    /// <summary>Block source path ("Program blocks/&lt;group&gt;/&lt;name&gt;").</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>BlockOfflineSignature rendered as 8 uppercase hex chars.</summary>
    public string Signature { get; set; } = string.Empty;
}

/// <summary>Read-only live software checksum for a PLC device.</summary>
public sealed class PlcChecksumInfo
{
    public string PlcName { get; set; } = string.Empty;
    public string ProjectIdentity { get; set; } = string.Empty;
    public string? SoftwareChecksum { get; set; }

    /// <summary>True when the PLC exposes the safety surface (SafetyAdministration or
    /// SafetySignatureProvider service present), i.e. it is a failsafe CPU. Null when produced
    /// by an older adapter that predates safety detection.</summary>
    public bool? IsSafetyDevice { get; set; }

    /// <summary>One of <see cref="FSignatureReadState"/>; null for non-safety devices and for
    /// older producers.</summary>
    public string? FSignatureReadState { get; set; }

    /// <summary>Collective offline F-signature folded (SHA-256, spaced uppercase hex pairs) from
    /// the per-F-block BlockOfflineSignature values (SafetySignatureProvider on each F-block,
    /// TIA Openness manual §5.27.4); null for non-failsafe PLCs or when unreadable.</summary>
    public string? FSignature { get; set; }

    /// <summary>The per-F-block signatures the fold was built from — recorded in commit-state
    /// tags and revision.json so a later compare can attribute a safety change to individual
    /// blocks instead of only flagging "something changed". Null for non-failsafe PLCs and
    /// older producers.</summary>
    public IReadOnlyList<FBlockSignatureInfo>? FBlockSignatures { get; set; }

    /// <summary>
    /// Content fingerprint folded from per-object FingerprintProvider values (blocks, UDTs,
    /// tag tables). Detects comment/text/interface edits that never move the compiled software
    /// checksum. Null when no object yielded a readable fingerprint.
    /// </summary>
    public string? ContentFingerprint { get; set; }

    /// <summary>True when TIA returned a compiled software checksum.</summary>
    public bool IsCompiled => !string.IsNullOrWhiteSpace(SoftwareChecksum);
}
