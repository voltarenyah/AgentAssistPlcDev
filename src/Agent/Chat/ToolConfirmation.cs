namespace Agent.Chat;

/// <summary>User decision on a destructive tool call (sandbox confirmation dialog).</summary>
public enum ToolConfirmation
{
    Deny,
    AllowOnce,
    AllowSession,
}

/// <summary>What the user is asked to approve before a destructive tool runs.</summary>
public sealed record ToolConfirmationRequest(
    string ToolName,
    string ArgumentsSummary,
    int DestructiveCallsSoFar,
    int SessionBudget);
