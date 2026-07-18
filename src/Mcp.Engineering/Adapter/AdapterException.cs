namespace Mcp.Engineering.Adapter;

/// <summary>Adapter-level failure carrying a §8.1 error code and optional remediation hint.</summary>
internal sealed class AdapterException : Exception
{
    public string Code { get; }
    public string? Remediation { get; }

    public AdapterException(string code, string message, string? remediation = null)
        : base(message)
    {
        Code = code;
        Remediation = remediation;
    }
}
