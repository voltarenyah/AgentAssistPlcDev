using Contracts.Sandbox;

namespace Mcp.Engineering.Sandbox;

/// <summary>
/// Server-side sandbox gate (approved proposal 2026-07-20): every tool call is classified
/// (default-deny unknown) and its path arguments jailed BEFORE the adapter runs — enforcement
/// holds for any MCP client, not just the chat agent. Denials are audited and raised as
/// <see cref="SandboxException"/>, which <see cref="Tools.EngineeringTools"/> maps to a
/// structured isError result like any adapter failure.
/// </summary>
public sealed class EngineeringGuard
{
    private readonly SandboxPolicy policy;
    private readonly PathJail jail;
    private readonly SandboxAudit audit;

    public EngineeringGuard(SandboxConfig config, SandboxAudit audit)
    {
        policy = config.Policy;
        jail = config.PathJail;
        this.audit = audit;
    }

    /// <summary>Classify the tool and jail its path arguments; returns the tier when the call may proceed.</summary>
    public SandboxTier Check(string tool, params (string Name, string? Value)[] pathArguments)
    {
        var tier = policy.Classify(tool);
        if (tier == null)
        {
            audit.Record(tool, "unknown", "deny", "not classified in the sandbox policy");
            throw new SandboxException(
                "SANDBOX_TOOL_UNKNOWN",
                $"Tool '{tool}' is not classified in the sandbox policy.",
                $"Add a tier for it in {SandboxConfig.DefaultFilePath}.");
        }

        if (tier == SandboxTier.Denied)
        {
            audit.Record(tool, SandboxTierNames.Name(tier.Value), "deny", "disabled by the sandbox policy");
            throw new SandboxException(
                "SANDBOX_TOOL_DENIED",
                $"Tool '{tool}' is disabled by the sandbox policy.",
                $"Change its tier in {SandboxConfig.DefaultFilePath} to re-enable it.");
        }

        foreach (var (name, value) in pathArguments)
        {
            if (value == null)
            {
                continue;
            }

            try
            {
                jail.Validate(value, name);
            }
            catch (SandboxException ex)
            {
                audit.Record(tool, SandboxTierNames.Name(tier.Value), "deny", $"{name}: {ex.Message}");
                throw;
            }
        }

        return tier.Value;
    }

    /// <summary>Audit a call that passed the gate and executed (tier + truncated argument detail).</summary>
    public void AuditAllow(string tool, SandboxTier tier, string? detail = null) =>
        audit.Record(tool, SandboxTierNames.Name(tier), "allow", detail);
}
