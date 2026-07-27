namespace Mcp.Knowledge.Import;

public sealed record ComponentImport(
    string ComponentKey,
    string RelativePath,
    string ContentHash,
    IReadOnlySet<string> NodeIds,
    IReadOnlySet<string> EdgeIds);

public sealed class ComponentIdentityMismatchException : Exception
{
    public ComponentIdentityMismatchException(string message)
        : base(message)
    {
    }

    public string Code => "COMPONENT_IDENTITY_MISMATCH";
}

public sealed class ComponentProvenanceUnavailableException : Exception
{
    public ComponentProvenanceUnavailableException(string message)
        : base(message)
    {
    }

    public string Code => "COMPONENT_PROVENANCE_REBUILD_REQUIRED";
}
