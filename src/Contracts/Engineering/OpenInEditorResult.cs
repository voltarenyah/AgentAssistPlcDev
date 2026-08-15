namespace Contracts.Engineering;

/// <summary>open_source_object_in_editor result: the source object was shown in the TIA Portal editor window.</summary>
public sealed class OpenInEditorResult
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? PlcName { get; set; }
    public bool Opened { get; set; }
}
