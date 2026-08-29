namespace Contracts.Engineering;

/// <summary>Read state of the offline collective F-signature (SafetySignatureProvider).
/// Serialized into commit-state tags and revision.json; values are stable strings.</summary>
public static class FSignatureReadState
{
    /// <summary>Safety device; the offline collective F-signature was read.</summary>
    public const string Ok = "ok";

    /// <summary>Safety device, but no signature is available (e.g. safety program not compiled).</summary>
    public const string NoSignature = "no-signature";

    /// <summary>Safety device whose signature read failed — never treat as an ordinary PLC.</summary>
    public const string ReadFailed = "read-failed";
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

    /// <summary>Offline collective F-signature (SafetySignatureProvider, BlockOfflineSignature)
    /// rendered as uppercase hex; null for non-failsafe PLCs or when unreadable.</summary>
    public string? FSignature { get; set; }

    /// <summary>True when TIA returned a compiled software checksum.</summary>
    public bool IsCompiled => !string.IsNullOrWhiteSpace(SoftwareChecksum);
}
