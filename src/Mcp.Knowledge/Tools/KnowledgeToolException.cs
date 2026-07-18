namespace Mcp.Knowledge.Tools;

/// <summary>
/// Tool-level failure carrying the structured error code for the isError envelope
/// (mirrors Mcp.Engineering's AdapterException pattern).
/// </summary>
internal sealed class KnowledgeToolException : Exception
{
    public KnowledgeToolException(string code, string message, string? remediation = null)
        : base(message)
    {
        Code = code;
        Remediation = remediation;
    }

    public string Code { get; }

    public string? Remediation { get; }
}
