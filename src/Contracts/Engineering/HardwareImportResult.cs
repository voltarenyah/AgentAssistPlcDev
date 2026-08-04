namespace Contracts.Engineering;

/// <summary>Result of importing a TIA Openness CAx hardware configuration.</summary>
public sealed class HardwareImportResult
{
    public bool Success { get; set; }
    public string AmlFilePath { get; set; } = string.Empty;
    public string? LogFilePath { get; set; }
    public HardwareImportConflictPolicy ConflictPolicy { get; set; }
    public string[] Warnings { get; set; } = Array.Empty<string>();
    public string? Error { get; set; }
    public DateTime ImportedAt { get; set; }
}
