using System.Collections.Concurrent;

namespace Agent.Workbench;

public sealed class WorkbenchRuntimeStateCoordinator
{
    private sealed class StateEntry(WorkbenchRuntimeSnapshot snapshot)
    {
        public object Gate { get; } = new();
        public WorkbenchRuntimeSnapshot Snapshot { get; set; } = snapshot;
    }

    private readonly ConcurrentDictionary<string, StateEntry> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string WorkbenchId, string RequestId), Lazy<WorkbenchRuntimeSnapshot>> requestResults = new();

    public event Action<WorkbenchRuntimeSnapshot>? StateChanged;

    public WorkbenchRuntimeSnapshot GetSnapshot(string workbenchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbenchId);
        return GetEntry(workbenchId).Snapshot;
    }

    public WorkbenchRuntimeSnapshot SetFocus(
        string workbenchId,
        string? worktreeId,
        string? deviceId,
        long? expectedRevision = null) =>
        Apply(new FocusChangedEvent(workbenchId, worktreeId, deviceId), expectedRevision);

    public WorkbenchRuntimeSnapshot Execute(RuntimeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RequestId);
        var key = (command.WorkbenchId, command.RequestId);
        var result = requestResults.GetOrAdd(
            key,
            _ => new Lazy<WorkbenchRuntimeSnapshot>(
                () => Dispatch(command),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return result.Value;
    }

    public WorkbenchRuntimeSnapshot Refresh(
        string workbenchId,
        IReadOnlyList<WorktreeRuntimeSummary> worktrees,
        long? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(worktrees);
        return Apply(new WorkbenchRefreshedEvent(workbenchId, worktrees), expectedRevision);
    }

    public WorkbenchRuntimeSnapshot ObserveWorktree(
        string workbenchId,
        WorktreeRuntimeSummary worktree,
        long? expectedRevision = null) =>
        Apply(new WorktreeObservedEvent(workbenchId, worktree), expectedRevision);

    public WorkbenchRuntimeSnapshot ObserveTodos(
        string workbenchId,
        string worktreeId,
        int todoCount,
        long? expectedRevision = null) =>
        Apply(new TodosObservedEvent(workbenchId, worktreeId, todoCount), expectedRevision);

    public WorkbenchRuntimeSnapshot ObserveHistory(
        string workbenchId,
        string worktreeId,
        string gitStatus,
        string? head,
        long? expectedRevision = null) =>
        Apply(new HistoryObservedEvent(workbenchId, worktreeId, gitStatus, head), expectedRevision);

    public WorkbenchRuntimeSnapshot ObserveSvnState(
        string workbenchId,
        string worktreeId,
        long? baseRevision,
        long? currentRevision,
        long? expectedRevision = null) =>
        Apply(new SvnStateObservedEvent(workbenchId, worktreeId, baseRevision, currentRevision), expectedRevision);

    public WorkbenchRuntimeSnapshot StartOperation(
        string workbenchId,
        string operationId,
        string kind,
        string? message = null,
        long? expectedRevision = null) =>
        Apply(new OperationStartedEvent(workbenchId, operationId, kind, message), expectedRevision);

    public WorkbenchRuntimeSnapshot CompleteOperation(
        string workbenchId,
        string operationId,
        string? message = null,
        long? expectedRevision = null) =>
        Apply(new OperationCompletedEvent(workbenchId, operationId, message), expectedRevision);

    public WorkbenchRuntimeSnapshot FailOperation(
        string workbenchId,
        string operationId,
        string message,
        long? expectedRevision = null) =>
        Apply(new OperationFailedEvent(workbenchId, operationId, message), expectedRevision);

    public WorkbenchRuntimeSnapshot Apply(WorkbenchRuntimeEvent @event, long? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var entry = GetEntry(@event.WorkbenchId);
        WorkbenchRuntimeSnapshot next;
        lock (entry.Gate)
        {
            var current = entry.Snapshot;
            if (expectedRevision is { } expected && expected != current.WorkbenchRevision)
                throw new RuntimeStateConflictException(expected, current.WorkbenchRevision);

            next = Reduce(current, @event);
            entry.Snapshot = next;
        }

        StateChanged?.Invoke(next);
        return next;
    }

    private WorkbenchRuntimeSnapshot Dispatch(RuntimeCommand command) => command switch
    {
        SelectWorkbenchCommand selected => Apply(
            new WorkbenchSelectedEvent(command.WorkbenchId, selected.WorktreeId, selected.DeviceId),
            command.ExpectedWorkbenchRevision),
        SetFocusCommand focus => SetFocus(
            command.WorkbenchId, focus.WorktreeId, focus.DeviceId, command.ExpectedWorkbenchRevision),
        RefreshWorkbenchCommand refreshed => Refresh(
            command.WorkbenchId, refreshed.Worktrees, command.ExpectedWorkbenchRevision),
        ObserveWorktreeCommand observed => ObserveWorktree(
            command.WorkbenchId, observed.Worktree, command.ExpectedWorkbenchRevision),
        ObserveTodosCommand todos => ObserveTodos(
            command.WorkbenchId, todos.WorktreeId, todos.TodoCount, command.ExpectedWorkbenchRevision),
        ObserveHistoryCommand history => ObserveHistory(
            command.WorkbenchId, history.WorktreeId, history.GitStatus, history.Head,
            command.ExpectedWorkbenchRevision),
        ObserveSvnStateCommand svn => ObserveSvnState(
            command.WorkbenchId, svn.WorktreeId, svn.BaseRevision, svn.CurrentRevision,
            command.ExpectedWorkbenchRevision),
        StartOperationCommand started => StartOperation(
            command.WorkbenchId, started.OperationId, started.Kind, started.Message,
            command.ExpectedWorkbenchRevision),
        CompleteOperationCommand completed => CompleteOperation(
            command.WorkbenchId, completed.OperationId, completed.Message,
            command.ExpectedWorkbenchRevision),
        FailOperationCommand failed => FailOperation(
            command.WorkbenchId, failed.OperationId, failed.Message,
            command.ExpectedWorkbenchRevision),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown runtime command."),
    };

    private StateEntry GetEntry(string workbenchId) =>
        entries.GetOrAdd(workbenchId, id => new StateEntry(Initial(id)));

    private static WorkbenchRuntimeSnapshot Initial(string workbenchId)
    {
        var snapshot = new WorkbenchRuntimeSnapshot(
            1,
            workbenchId,
            0,
            new WorkbenchFocus(null, null),
            Array.Empty<WorktreeRuntimeSummary>(),
            RuntimeOperation.Idle,
            Array.Empty<ActionCapability>(),
            DateTimeOffset.UtcNow);
        return snapshot with { AvailableActions = ActionsFor(snapshot) };
    }

    private static WorkbenchRuntimeSnapshot Reduce(
        WorkbenchRuntimeSnapshot current,
        WorkbenchRuntimeEvent @event)
    {
        var updated = @event switch
        {
            WorkbenchSelectedEvent selected => current with
            {
                Focus = new WorkbenchFocus(selected.WorktreeId, selected.DeviceId),
            },
            FocusChangedEvent focus => current with
            {
                Focus = new WorkbenchFocus(focus.WorktreeId, focus.DeviceId),
            },
            WorkbenchRefreshedEvent refreshed => current with
            {
                Worktrees = refreshed.Worktrees.ToArray(),
            },
            WorktreeObservedEvent observed => current with
            {
                Worktrees = UpsertWorktree(current.Worktrees, observed.Worktree),
            },
            TodosObservedEvent todos => current with
            {
                Worktrees = UpdateWorktree(current.Worktrees, todos.WorktreeId,
                    worktree => worktree with { TodoCount = todos.TodoCount }),
            },
            HistoryObservedEvent history => current with
            {
                Worktrees = UpdateWorktree(current.Worktrees, history.WorktreeId,
                    worktree => worktree with { GitStatus = history.GitStatus, Head = history.Head }),
            },
            SvnStateObservedEvent svn => current with
            {
                Worktrees = UpdateWorktree(current.Worktrees, svn.WorktreeId,
                    worktree => worktree with
                    {
                        SvnBaseRevision = svn.BaseRevision,
                        SvnCurrentRevision = svn.CurrentRevision,
                    }),
            },
            OperationStartedEvent started => current with
            {
                Operation = new RuntimeOperation(
                    started.OperationId,
                    started.Kind,
                    RuntimeOperationStatus.Running,
                    started.Message),
            },
            OperationCompletedEvent completed => current with
            {
                Operation = new RuntimeOperation(
                    completed.OperationId,
                    current.Operation.Kind,
                    RuntimeOperationStatus.Succeeded,
                    completed.Message),
            },
            OperationFailedEvent failed => current with
            {
                Operation = new RuntimeOperation(
                    failed.OperationId,
                    current.Operation.Kind,
                    RuntimeOperationStatus.Failed,
                    failed.Message),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(@event), @event, "Unknown runtime event."),
        };

        var versioned = updated with
        {
            WorkbenchRevision = current.WorkbenchRevision + 1,
            ObservedAt = DateTimeOffset.UtcNow,
        };
        return versioned with { AvailableActions = ActionsFor(versioned) };
    }

    private static IReadOnlyList<WorktreeRuntimeSummary> UpsertWorktree(
        IReadOnlyList<WorktreeRuntimeSummary> current,
        WorktreeRuntimeSummary worktree)
    {
        var updated = current.Where(item => item.WorktreeId != worktree.WorktreeId)
            .Append(worktree)
            .ToArray();
        return updated;
    }

    private static IReadOnlyList<WorktreeRuntimeSummary> UpdateWorktree(
        IReadOnlyList<WorktreeRuntimeSummary> current,
        string worktreeId,
        Func<WorktreeRuntimeSummary, WorktreeRuntimeSummary> update)
    {
        var found = false;
        var updated = current.Select(worktree =>
        {
            if (worktree.WorktreeId != worktreeId)
                return worktree;
            found = true;
            return update(worktree);
        }).ToList();
        if (!found)
            throw new KeyNotFoundException($"WORKTREE_NOT_FOUND: {worktreeId}");
        return updated;
    }

    private static IReadOnlyList<ActionCapability> ActionsFor(WorkbenchRuntimeSnapshot snapshot)
    {
        var operationBusy = snapshot.Operation.Status is
            RuntimeOperationStatus.Running or RuntimeOperationStatus.AwaitingApproval;
        var hasWorktree = snapshot.Worktrees.Count > 0;
        var blocked = operationBusy
            ? new[] { "Another workbench operation is running." }
            : Array.Empty<string>();

        return new ActionCapability[]
        {
            new("read_worktree_todos", "Read worktree todo lists", "worktree", hasWorktree, false,
                hasWorktree ? Array.Empty<string>() : new[] { "No worktree is registered." }),
            new("read_commit_history", "Read worktree commit history", "worktree", hasWorktree, false,
                hasWorktree ? Array.Empty<string>() : new[] { "No worktree is registered." }),
            new("read_svn_state", "Read SVN revision state", "workbench", true, false, Array.Empty<string>()),
            new("create_worktree", "Create a worktree", "workbench", !operationBusy, true, blocked),
        };
    }
}
