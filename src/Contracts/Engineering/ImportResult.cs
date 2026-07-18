namespace Contracts.Engineering;

/// <summary>import_block result (mcp-engineering.md §6.2).</summary>
public sealed class ImportResult
{
    public string BlockName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string[] Warnings { get; set; } = Array.Empty<string>();

    /// <summary>True when the re-export compare actually ran (false = verify deferred/failed — see warnings).</summary>
    public bool InterfaceVerified { get; set; }

    /// <summary>True when the post-import re-export differs from the imported interface (§6.1 item 5).</summary>
    public bool InterfaceDrift { get; set; }

    public string? Error { get; set; }
    public DateTime ImportedAt { get; set; }
}
