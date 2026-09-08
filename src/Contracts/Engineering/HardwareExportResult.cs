namespace Contracts.Engineering;

/// <summary>Result of exporting one project- or device-level TIA Openness CAx artifact.</summary>
public sealed class HardwareExportResult
{
    public string Scope { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? TypeIdentifier { get; set; }
    public string? AmlFilePath { get; set; }
    public string? LogFilePath { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ContentHash { get; set; }
    public DateTime ExportedAt { get; set; }

    /// <summary>Wall-clock duration of the CAx export operation for this artifact.</summary>
    public long? DurationMs { get; set; }

    /// <summary>Wall-clock duration of the project-level network fingerprint capture. This is
    /// populated on the project result because the fingerprint is a project-level supplement,
    /// not a CAx artifact of its own.</summary>
    public long? NetworkConfigurationDurationMs { get; set; }
}
