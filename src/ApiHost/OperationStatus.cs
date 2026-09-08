using System.Collections.Concurrent;
using Agent.Workbench;

public sealed record OperationStatusSnapshot(
    string OperationId,
    string OperationType,
    string State,
    string Message,
    DateTimeOffset UpdatedAt,
    string? ErrorMessage,
    IReadOnlyList<OperationPhaseTiming>? CompletedPhases = null,
    OperationPhaseTiming? CurrentPhase = null);

/// <summary>One user-visible operation phase. Completed phases retain their measured duration;
/// the active phase duration is projected when the status endpoint is read.</summary>
public sealed record OperationPhaseTiming(
    string Message,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long ElapsedMilliseconds);

public sealed class OperationStatusRegistry
{
    private static readonly TimeSpan TerminalLifetime = TimeSpan.FromMinutes(60);
    private const string ExportCounterMessagePrefix = "Exported PLC source files: ";
    private readonly ConcurrentDictionary<string, OperationStatusSnapshot> snapshots = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;

    public OperationStatusRegistry()
        : this(TimeProvider.System)
    {
    }

    public OperationStatusRegistry(TimeProvider clock) =>
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public void Start(string operationId, string operationType, string message)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        var startedAt = clock.GetUtcNow();
        snapshots[operationId] = new OperationStatusSnapshot(
            operationId,
            operationType,
            "running",
            message,
            startedAt,
            null,
            [],
            StartPhase(message, startedAt));
    }

    public void Report(string operationId, string message)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var reportedAt = clock.GetUtcNow();
        snapshots.AddOrUpdate(
            operationId,
            id => new OperationStatusSnapshot(
                id,
                "operation",
                "running",
                message,
                reportedAt,
                null,
                [],
                StartPhase(message, reportedAt)),
            (_, current) => AdvancePhase(current, message, reportedAt));
    }

    public void Succeed(string operationId, string message)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        var completedAt = clock.GetUtcNow();
        snapshots.AddOrUpdate(
            operationId,
            id => new OperationStatusSnapshot(id, "operation", "succeeded", message, completedAt, null, []),
            (_, current) => Complete(current, "succeeded", message, null, completedAt));
    }

    public void Fail(string operationId, string message, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        var failedAt = clock.GetUtcNow();
        snapshots.AddOrUpdate(
            operationId,
            id => new OperationStatusSnapshot(id, "operation", "failed", message, failedAt, errorMessage, []),
            (_, current) => Complete(
                current,
                "failed",
                string.IsNullOrWhiteSpace(message) ? current.Message : message,
                errorMessage,
                failedAt));
    }

    public IOperationProgress For(string operationId) => new RegistryProgress(this, operationId);

    public bool TryGet(string operationId, out OperationStatusSnapshot snapshot)
    {
        snapshot = default!;
        if (string.IsNullOrWhiteSpace(operationId)
            || !snapshots.TryGetValue(operationId, out var current))
        {
            return false;
        }

        if (current.State is "succeeded" or "failed"
            && clock.GetUtcNow() - current.UpdatedAt > TerminalLifetime)
        {
            snapshots.TryRemove(operationId, out _);
            return false;
        }

        snapshot = ProjectCurrentElapsed(current, clock.GetUtcNow());
        return true;
    }

    public void Dismiss(string operationId)
    {
        if (!string.IsNullOrWhiteSpace(operationId))
        {
            snapshots.TryRemove(operationId, out _);
        }
    }

    private sealed class RegistryProgress(OperationStatusRegistry owner, string operationId) : IOperationProgress
    {
        public void Report(string message) => owner.Report(operationId, message);
    }

    private static OperationStatusSnapshot AdvancePhase(
        OperationStatusSnapshot current,
        string message,
        DateTimeOffset reportedAt)
    {
        if (IsExportCounter(message)
            || string.Equals(current.CurrentPhase?.Message, message, StringComparison.Ordinal))
        {
            return current with { Message = message, UpdatedAt = reportedAt };
        }

        var completed = CompleteCurrentPhase(current.CompletedPhases, current.CurrentPhase, reportedAt);
        return current with
        {
            Message = message,
            UpdatedAt = reportedAt,
            CompletedPhases = completed,
            CurrentPhase = StartPhase(message, reportedAt),
        };
    }

    private static OperationStatusSnapshot Complete(
        OperationStatusSnapshot current,
        string state,
        string message,
        string? errorMessage,
        DateTimeOffset completedAt) =>
        current with
        {
            State = state,
            Message = message,
            UpdatedAt = completedAt,
            ErrorMessage = errorMessage,
            CompletedPhases = CompleteCurrentPhase(current.CompletedPhases, current.CurrentPhase, completedAt),
            CurrentPhase = null,
        };

    private static IReadOnlyList<OperationPhaseTiming> CompleteCurrentPhase(
        IReadOnlyList<OperationPhaseTiming>? completed,
        OperationPhaseTiming? current,
        DateTimeOffset completedAt)
    {
        var result = completed?.ToList() ?? [];
        if (current is not null)
        {
            result.Add(current with
            {
                CompletedAt = completedAt,
                ElapsedMilliseconds = ElapsedMilliseconds(current.StartedAt, completedAt),
            });
        }

        return result;
    }

    private static OperationStatusSnapshot ProjectCurrentElapsed(
        OperationStatusSnapshot snapshot,
        DateTimeOffset observedAt) =>
        snapshot.CurrentPhase is null
            ? snapshot
            : snapshot with
            {
                CurrentPhase = snapshot.CurrentPhase with
                {
                    ElapsedMilliseconds = ElapsedMilliseconds(snapshot.CurrentPhase.StartedAt, observedAt),
                },
            };

    private static OperationPhaseTiming StartPhase(string message, DateTimeOffset startedAt) =>
        new(message, startedAt, null, 0);

    private static long ElapsedMilliseconds(DateTimeOffset startedAt, DateTimeOffset finishedAt) =>
        Math.Max(0, (long)(finishedAt - startedAt).TotalMilliseconds);

    private static bool IsExportCounter(string message) =>
        message.StartsWith(ExportCounterMessagePrefix, StringComparison.Ordinal);
}
