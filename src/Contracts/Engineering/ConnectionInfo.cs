namespace Contracts.Engineering;

/// <summary>Result of a successful connect.</summary>
public sealed class ConnectionInfo
{
    public bool Attached { get; set; }
    public bool HasUI { get; set; }
    public string? ProjectName { get; set; }
    public string? ProjectPath { get; set; }
}
