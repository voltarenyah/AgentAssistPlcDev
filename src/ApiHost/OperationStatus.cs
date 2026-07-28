using System.Collections.Concurrent;
using Agent.Workbench;

public sealed record OperationStatusSnapshot(
    string OperationId,
    string OperationType,
    string State,
    string Message,
    DateTimeOffset UpdatedAt,
    string? ErrorMessage);

public sealed class OperationStatusRegistry
{
    private static readonly TimeSpan TerminalLifetime = TimeSpan.FromMinutes(60);
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

        snapshots[operationId] = new OperationStatusSnapshot(
            operationId,
            operationType,
            "running",
            message,
            clock.GetUtcNow(),
            null);
    }

    public void Report(string operationId, string message)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        snapshots.AddOrUpdate(
            operationId,
            id => new OperationStatusSnapshot(id, "operation", "running", message, clock.GetUtcNow(), null),
            (_, current) => current with
            {
                Message = message,
                UpdatedAt = clock.GetUtcNow(),
            });
    }

    public void Succeed(string operationId, string message)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        snapshots.AddOrUpdate(
            operationId,
            id => new OperationStatusSnapshot(id, "operation", "succeeded", message, clock.GetUtcNow(), null),
            (_, current) => current with
            {
                State = "succeeded",
                Message = message,
                UpdatedAt = clock.GetUtcNow(),
                ErrorMessage = null,
            });
    }

    public void Fail(string operationId, string message, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        snapshots.AddOrUpdate(
            operationId,
            id => new OperationStatusSnapshot(id, "operation", "failed", message, clock.GetUtcNow(), errorMessage),
            (_, current) => current with
            {
                State = "failed",
                Message = string.IsNullOrWhiteSpace(message) ? current.Message : message,
                UpdatedAt = clock.GetUtcNow(),
                ErrorMessage = errorMessage,
            });
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

        snapshot = current;
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
}
