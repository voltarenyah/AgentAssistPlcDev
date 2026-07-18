namespace Contracts.Engineering;

/// <summary>One structured compiler message (mcp-engineering.md §7.4).</summary>
public sealed class CompileMessage
{
    /// <summary>error | warning | info</summary>
    public string Type { get; set; } = "info";

    public string? Code { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? BlockName { get; set; }
    public int? NetworkNumber { get; set; }
    public int? Line { get; set; }
}
