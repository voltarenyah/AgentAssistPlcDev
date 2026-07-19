namespace App.Mcp;

/// <summary>An MCP tool returned isError=true with the servers' structured { code, message, remediation } payload.</summary>
public sealed class ToolCallException : Exception
{
    public ToolCallException(string code, string message, string? remediation)
        : base(message)
    {
        Code = code;
        Remediation = remediation;
    }

    public string Code { get; }

    public string? Remediation { get; }
}
