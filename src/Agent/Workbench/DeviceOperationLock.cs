using System.Collections.Concurrent;

namespace Agent.Workbench;

public sealed class DeviceOperationLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.Ordinal);

    public ValueTask<IAsyncDisposable> AcquireAsync(
        DeviceContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return AcquireAsync(KeyFor(context), cancellationToken);
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A device ID is required.", nameof(deviceId));
        }

        var semaphore = Locks.GetOrAdd(deviceId, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    public Task RunAsync(
        DeviceContext context,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        return RunAsync(KeyFor(context), operation, cancellationToken);
    }

    public async Task RunAsync(
        string deviceId,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var lease = await AcquireAsync(deviceId, cancellationToken)
            .ConfigureAwait(false);
        await operation(cancellationToken).ConfigureAwait(false);
    }

    public Task<T> RunAsync<T>(
        DeviceContext context,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);
        return RunAsync(KeyFor(context), operation, cancellationToken);
    }

    public async Task<T> RunAsync<T>(
        string deviceId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var lease = await AcquireAsync(deviceId, cancellationToken)
            .ConfigureAwait(false);
        return await operation(cancellationToken).ConfigureAwait(false);
    }

    private static string KeyFor(DeviceContext context) =>
        string.Join('\0', context.WorkbenchId, context.WorktreeId, context.DeviceId);

    private sealed class Releaser : IAsyncDisposable, IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
