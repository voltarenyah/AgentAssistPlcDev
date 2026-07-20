using System.Text.Json;
using Contracts.Sandbox;

namespace Agent.Chat;

/// <summary>
/// Agent-side sandbox gate (approved proposal 2026-07-20): classifies every model-requested tool
/// call before dispatch — unknown tools denied (fail closed), "deny"-tier tools blocked, destructive
/// tools need user confirmation (per-call or session grant) within a per-session budget. Every
/// decision lands in the JSONL audit trail. The server-side counterpart (path jail + deny tiers,
/// Mcp.Engineering's EngineeringGuard) enforces the same policy for any other MCP client.
/// </summary>
public sealed class AgentSandbox
{
    private const int ArgumentsSummaryMaxChars = 160;

    /// <summary>Gate outcome: the call is blocked; <see cref="ErrorJson"/> goes to the model, <see cref="Note"/> to the UI.</summary>
    public sealed record Verdict(string ErrorJson, string Note);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SandboxPolicy policy;
    private readonly SandboxAudit? audit;
    private readonly Func<ToolConfirmationRequest, Task<ToolConfirmation>>? confirm;
    private readonly HashSet<string> sessionGranted = new(StringComparer.Ordinal);

    public AgentSandbox(
        SandboxPolicy policy,
        int maxDestructiveCallsPerSession,
        Func<ToolConfirmationRequest, Task<ToolConfirmation>>? confirm = null,
        SandboxAudit? audit = null)
    {
        this.policy = policy;
        this.confirm = confirm;
        this.audit = audit;
        MaxDestructiveCallsPerSession = Math.Max(0, maxDestructiveCallsPerSession);
    }

    public int MaxDestructiveCallsPerSession { get; }

    public int DestructiveCallsSoFar { get; private set; }

    /// <summary>Returns null when the call may proceed; otherwise the verdict to hand back without dispatching.</summary>
    public async Task<Verdict?> CheckAsync(ChatToolCall call, CancellationToken cancellationToken = default)
    {
        var tier = policy.Classify(call.Name);
        switch (tier)
        {
            case null:
                return Block(call, "unknown", "SANDBOX_TOOL_UNKNOWN",
                    $"Tool '{call.Name}' is not classified in the sandbox policy.",
                    $"Add a tier for it in {SandboxConfig.DefaultFilePath}.");
            case SandboxTier.Denied:
                return Block(call, "denied", "SANDBOX_TOOL_DENIED",
                    $"Tool '{call.Name}' is disabled by the sandbox policy.", null);
            case SandboxTier.Destructive:
                return await CheckDestructiveAsync(call, cancellationToken);
            default:
                audit?.Record(call.Name, SandboxTierNames.Name(tier.Value), "allow", Summarize(call.ArgumentsJson));
                return null;
        }
    }

    private async Task<Verdict?> CheckDestructiveAsync(ChatToolCall call, CancellationToken cancellationToken)
    {
        if (sessionGranted.Contains(call.Name))
        {
            audit?.Record(call.Name, "destructive", "allow-session", Summarize(call.ArgumentsJson));
            return null;
        }

        if (DestructiveCallsSoFar >= MaxDestructiveCallsPerSession)
        {
            return Block(call, "destructive", "SANDBOX_BUDGET_EXCEEDED",
                $"Session budget of {MaxDestructiveCallsPerSession} destructive call(s) is exhausted.",
                "Restart the session or raise maxDestructiveCallsPerSession in sandbox.json.");
        }

        if (confirm == null)
        {
            return Block(call, "destructive", "SANDBOX_NO_CONFIRMATION",
                $"Tool '{call.Name}' is destructive and this host offers no confirmation channel.",
                "Run it from the App chat, where the user can approve it.");
        }

        var decision = await confirm(new ToolConfirmationRequest(
            call.Name, Summarize(call.ArgumentsJson), DestructiveCallsSoFar, MaxDestructiveCallsPerSession));
        cancellationToken.ThrowIfCancellationRequested();

        switch (decision)
        {
            case ToolConfirmation.AllowOnce:
                DestructiveCallsSoFar++;
                audit?.Record(call.Name, "destructive", "allow-once", Summarize(call.ArgumentsJson));
                return null;
            case ToolConfirmation.AllowSession:
                DestructiveCallsSoFar++;
                sessionGranted.Add(call.Name);
                audit?.Record(call.Name, "destructive", "allow-session", Summarize(call.ArgumentsJson));
                return null;
            default:
                return Block(call, "destructive", "SANDBOX_USER_DENIED",
                    $"The user denied the destructive tool '{call.Name}'.",
                    "Ask the user before retrying it.");
        }
    }

    private Verdict Block(ChatToolCall call, string tier, string code, string message, string? remediation)
    {
        audit?.Record(call.Name, tier, "deny", $"{code}: {message}");
        var errorJson = JsonSerializer.Serialize(new { error = new { code, message, remediation } }, Json);
        return new Verdict(errorJson, message);
    }

    private static string Summarize(string argumentsJson) =>
        argumentsJson.Length <= ArgumentsSummaryMaxChars
            ? argumentsJson
            : argumentsJson[..ArgumentsSummaryMaxChars] + "…";
}
