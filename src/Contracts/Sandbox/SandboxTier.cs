namespace Contracts.Sandbox;

/// <summary>
/// Permission tier of an MCP tool (agent sandbox, approved 2026-07-20): Read auto-allow,
/// Write allow + audit, Destructive requires user confirmation, Denied is blocked outright.
/// Tools missing from the policy are treated as unknown and denied (fail closed).
/// </summary>
public enum SandboxTier
{
    Read,
    Write,
    Destructive,
    Denied,
}

public static class SandboxTierNames
{
    public static string Name(SandboxTier tier) => tier switch
    {
        SandboxTier.Read => "read",
        SandboxTier.Write => "write",
        SandboxTier.Destructive => "destructive",
        SandboxTier.Denied => "denied",
        _ => "unknown",
    };

    /// <summary>Parses a config tier value ("read"|"write"|"destructive"|"deny"|"denied"). Unrecognized values fail closed → Denied.</summary>
    public static SandboxTier Parse(string text) => text.Trim().ToLowerInvariant() switch
    {
        "read" => SandboxTier.Read,
        "write" => SandboxTier.Write,
        "destructive" => SandboxTier.Destructive,
        _ => SandboxTier.Denied,
    };
}
