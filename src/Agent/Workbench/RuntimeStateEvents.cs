namespace Agent.Workbench;

public sealed record RuntimeRevision(long WorkbenchRevision, DateTimeOffset ObservedAt);

public abstract record RuntimeCommand(
    string WorkbenchId,
    string RequestId,
    long? ExpectedWorkbenchRevision,
    string RequestedBy);

public sealed record SelectWorkbenchCommand(
    string WorkbenchId,
    string RequestId,
    long? ExpectedWorkbenchRevision,
    string RequestedBy,
    string? WorktreeId,
    string? DeviceId)
    : RuntimeCommand(WorkbenchId, RequestId, ExpectedWorkbenchRevision, RequestedBy);

public sealed record SetFocusCommand(
    string WorkbenchId,
    string RequestId,
    long? ExpectedWorkbenchRevision,
    string RequestedBy,
    string? WorktreeId,
    string? DeviceId)
    : RuntimeCommand(WorkbenchId, RequestId, ExpectedWorkbenchRevision, RequestedBy);

public sealed record RefreshWorkbenchCommand(
    string WorkbenchId,
    string RequestId,
    long? ExpectedWorkbenchRevision,
    string RequestedBy,
    IReadOnlyList<WorktreeRuntimeSummary> Worktrees)
    : RuntimeCommand(WorkbenchId, RequestId, ExpectedWorkbenchRevision, RequestedBy);

public sealed record ObserveWorktreeCommand(
    string WorkbenchId,
    string RequestId,
    long? ExpectedWorkbenchRevision,
    string RequestedBy,
    WorktreeRuntimeSummary Worktree)
    : RuntimeCommand(WorkbenchId, RequestId, ExpectedWorkbenchRevision, RequestedBy);

public sealed record ObserveTodosCommand(
    string WorkbenchId,
    string RequestId,
    long? ExpectedWorkbenchRevision,
    string RequestedBy,
    string WorktreeId,
    int TodoCount)
    : RuntimeCommand(WorkbenchId, RequestId, ExpectedWorkbenchRevision, RequestedBy);

public sealed record ObserveHistoryCommand(
    string WorkbenchId,
    string RequestId,
    long? ExpectedWorkbenchRevision,
    string RequestedBy,
    string WorktreeId,
    string GitStatus,
    string? Head)
    : RuntimeCommand(WorkbenchId, RequestId, ExpectedWorkbenchRevision, RequestedBy);

public sealed record ObserveSvnStateCommand(
    string WorkbenchId,
    string RequestId,
    long? ExpectedWorkbenchRevision,
    string RequestedBy,
    string WorktreeId,
    long? BaseRevision,
    long? CurrentRevision)
    : RuntimeCommand(WorkbenchId, RequestId, ExpectedWorkbenchRevision, RequestedBy);

public sealed record StartOperationCommand(
    string WorkbenchId,
    string RequestId,
    long? ExpectedWorkbenchRevision,
    string RequestedBy,
    string OperationId,
    string Kind,
    string? Message)
    : RuntimeCommand(WorkbenchId, RequestId, ExpectedWorkbenchRevision, RequestedBy);

public sealed record CompleteOperationCommand(
    string WorkbenchId,
    string RequestId,
    long? ExpectedWorkbenchRevision,
    string RequestedBy,
    string OperationId,
    string? Message)
    : RuntimeCommand(WorkbenchId, RequestId, ExpectedWorkbenchRevision, RequestedBy);

public sealed record FailOperationCommand(
    string WorkbenchId,
    string RequestId,
    long? ExpectedWorkbenchRevision,
    string RequestedBy,
    string OperationId,
    string Message)
    : RuntimeCommand(WorkbenchId, RequestId, ExpectedWorkbenchRevision, RequestedBy);

public abstract record WorkbenchRuntimeEvent(string WorkbenchId);

public sealed record WorkbenchSelectedEvent(
    string WorkbenchId,
    string? WorktreeId,
    string? DeviceId)
    : WorkbenchRuntimeEvent(WorkbenchId);

public sealed record FocusChangedEvent(
    string WorkbenchId,
    string? WorktreeId,
    string? DeviceId)
    : WorkbenchRuntimeEvent(WorkbenchId);

public sealed record WorkbenchRefreshedEvent(
    string WorkbenchId,
    IReadOnlyList<WorktreeRuntimeSummary> Worktrees)
    : WorkbenchRuntimeEvent(WorkbenchId);

public sealed record WorktreeObservedEvent(
    string WorkbenchId,
    WorktreeRuntimeSummary Worktree)
    : WorkbenchRuntimeEvent(WorkbenchId);

public sealed record TodosObservedEvent(
    string WorkbenchId,
    string WorktreeId,
    int TodoCount)
    : WorkbenchRuntimeEvent(WorkbenchId);

public sealed record HistoryObservedEvent(
    string WorkbenchId,
    string WorktreeId,
    string GitStatus,
    string? Head)
    : WorkbenchRuntimeEvent(WorkbenchId);

public sealed record SvnStateObservedEvent(
    string WorkbenchId,
    string WorktreeId,
    long? BaseRevision,
    long? CurrentRevision)
    : WorkbenchRuntimeEvent(WorkbenchId);

public sealed record OperationStartedEvent(
    string WorkbenchId,
    string OperationId,
    string Kind,
    string? Message)
    : WorkbenchRuntimeEvent(WorkbenchId);

public sealed record OperationCompletedEvent(
    string WorkbenchId,
    string OperationId,
    string? Message)
    : WorkbenchRuntimeEvent(WorkbenchId);

public sealed record OperationFailedEvent(
    string WorkbenchId,
    string OperationId,
    string Message)
    : WorkbenchRuntimeEvent(WorkbenchId);
