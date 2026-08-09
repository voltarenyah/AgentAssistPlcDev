namespace Agent.Workbench;

public sealed record WorkbenchFocus(string? WorktreeId, string? DeviceId);

public enum RuntimeOperationStatus
{
    Idle,
    Running,
    AwaitingApproval,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record RuntimeOperation(
    string? OperationId,
    string? Kind,
    RuntimeOperationStatus Status,
    string? Message)
{
    public static RuntimeOperation Idle { get; } = new(null, null, RuntimeOperationStatus.Idle, null);
}

public sealed record DeviceRuntimeSummary(
    string DeviceId,
    string? PlcName,
    string TiaState,
    string KnowledgeFreshness);

public sealed record WorktreeRuntimeSummary(
    string WorktreeId,
    string Name,
    string Branch,
    string GitStatus,
    string? Head,
    int TodoCount,
    long? SvnBaseRevision,
    long? SvnCurrentRevision,
    string ValidationState,
    IReadOnlyList<DeviceRuntimeSummary> Devices);

public sealed record ActionCapability(
    string Id,
    string Label,
    string Scope,
    bool Enabled,
    bool RequiresApproval,
    IReadOnlyList<string> BlockedBy);

public sealed record WorkbenchRuntimeSnapshot(
    int SchemaVersion,
    string WorkbenchId,
    long WorkbenchRevision,
    WorkbenchFocus Focus,
    IReadOnlyList<WorktreeRuntimeSummary> Worktrees,
    RuntimeOperation Operation,
    IReadOnlyList<ActionCapability> AvailableActions,
    DateTimeOffset ObservedAt);

public sealed class RuntimeStateConflictException(
    long expectedRevision,
    long actualRevision)
    : InvalidOperationException(
        $"Runtime state revision is stale. Expected {expectedRevision}, actual {actualRevision}.")
{
    public string Code => "CONTEXT_STALE";
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}
