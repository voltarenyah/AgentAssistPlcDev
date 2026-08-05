namespace Contracts.Engineering;

/// <summary>Read-only live software checksum for a PLC device.</summary>
public sealed class PlcChecksumInfo
{
    public string PlcName { get; set; } = string.Empty;
    public string ProjectIdentity { get; set; } = string.Empty;
    public string? SoftwareChecksum { get; set; }

    /// <summary>True when TIA returned a compiled software checksum.</summary>
    public bool IsCompiled => !string.IsNullOrWhiteSpace(SoftwareChecksum);
}
