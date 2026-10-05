namespace Agent.Chat;

/// <summary>User decision on a destructive tool call (sandbox confirmation dialog).</summary>
public enum ToolConfirmation
{
    Deny,
    AllowOnce,
    AllowSession,

    /// <summary>The card was never answered and its deadline passed. It is a distinct outcome from
    /// <see cref="Deny"/> so a timeout is never reported as the user having rejected the call.</summary>
    Expired,
}

/// <summary>What the user is asked to approve before a destructive tool runs.</summary>
public sealed record ToolConfirmationRequest(
    string ToolName,
    string ArgumentsSummary,
    int DestructiveCallsSoFar,
    int SessionBudget);
