namespace Contracts.Engineering;

/// <summary>export_block / export_all_blocks entry (mcp-engineering.md §5.2).</summary>
public sealed class ExportResult
{
    public string BlockName { get; set; } = string.Empty;
    public int? BlockNumber { get; set; }
    public string? BlockType { get; set; }
    public string? Path { get; set; }
    public int? NetworkCount { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public DateTime ExportedAt { get; set; }
}
