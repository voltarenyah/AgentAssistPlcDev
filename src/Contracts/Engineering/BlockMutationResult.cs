namespace Contracts.Engineering;

/// <summary>Result of a block create/delete operation.</summary>
public sealed class BlockMutationResult
{
    public string BlockName { get; set; } = string.Empty;
    public string? BlockType { get; set; }
    public int? BlockNumber { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public DateTime ChangedAt { get; set; }
}
