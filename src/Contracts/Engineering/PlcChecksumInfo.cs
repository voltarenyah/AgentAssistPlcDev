namespace Contracts.Engineering;

/// <summary>Read-only live software checksum for a PLC device.</summary>
public sealed class PlcChecksumInfo
{
    public string PlcName { get; set; } = string.Empty;
    public string ProjectIdentity { get; set; } = string.Empty;
    public string? SoftwareChecksum { get; set; }

    /// <summary>
    /// Content fingerprint folded from per-object FingerprintProvider values (blocks, UDTs,
    /// tag tables). Detects comment/text/interface edits that never move the compiled software
    /// checksum. Null when no object yielded a readable fingerprint.
    /// </summary>
    public string? ContentFingerprint { get; set; }

    /// <summary>True when TIA returned a compiled software checksum.</summary>
    public bool IsCompiled => !string.IsNullOrWhiteSpace(SoftwareChecksum);
}
