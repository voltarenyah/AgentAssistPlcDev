namespace Contracts.Sandbox;

/// <summary>Sandbox denial, carried like AdapterException: structured code + optional remediation hint.</summary>
public sealed class SandboxException : Exception
{
    public SandboxException(string code, string message, string? remediation = null)
        : base(message)
    {
        Code = code;
        Remediation = remediation;
    }

    public string Code { get; }

    public string? Remediation { get; }
}
