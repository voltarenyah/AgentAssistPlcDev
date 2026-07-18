namespace Contracts.Engineering;

/// <summary>compile_block / compile_plc result (mcp-engineering.md §7).</summary>
public sealed class CompileResult
{
    /// <summary>Null = whole PLC software (compile_plc).</summary>
    public string? BlockName { get; set; }

    /// <summary>pending | success | warnings | error</summary>
    public string State { get; set; } = "pending";

    public CompileMessage[] Messages { get; set; } = Array.Empty<CompileMessage>();
    public long DurationMs { get; set; }
}
