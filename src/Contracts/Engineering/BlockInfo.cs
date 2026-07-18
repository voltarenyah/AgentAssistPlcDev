namespace Contracts.Engineering;

/// <summary>list_blocks entry.</summary>
public sealed class BlockInfo
{
    public string Name { get; set; } = string.Empty;
    public int Number { get; set; }
    public string BlockType { get; set; } = string.Empty;
    public string? ProgrammingLanguage { get; set; }

    /// <summary>Path of nested block groups under the root group (null = root).</summary>
    public string? GroupPath { get; set; }
}
