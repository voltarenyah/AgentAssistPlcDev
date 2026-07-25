namespace Mcp.VersionControl.Tools;

/// <summary>Structured exception for version-control tool failures.</summary>
internal sealed class VcException : Exception
{
    public VcException(string code, string message, string? remediation = null)
        : base(message)
    {
        Code = code;
        Remediation = remediation ?? string.Empty;
    }

    public string Code { get; }
    public string Remediation { get; }
}
