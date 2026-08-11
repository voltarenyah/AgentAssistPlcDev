using System.Text.Json;
using System.Threading.Channels;
using Agent.Workbench;

public static class RuntimeStateEventEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeStateEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/workbenches/{workbenchId}/runtime-events",
            async (HttpContext http, string workbenchId, WorkbenchApiState state,
                WorkbenchRuntimeStateCoordinator runtime) =>
            {
                state.RefreshRuntimeIfChanged(workbenchId);
                http.Response.StatusCode = StatusCodes.Status200OK;
                http.Response.ContentType = "text/event-stream";
                http.Response.Headers.CacheControl = "no-cache";

                var snapshots = Channel.CreateBounded<WorkbenchRuntimeSnapshot>(
                    new BoundedChannelOptions(16)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                        SingleWriter = false,
                    });
                void OnStateChanged(WorkbenchRuntimeSnapshot snapshot)
                {
                    if (snapshot.WorkbenchId == workbenchId)
                        snapshots.Writer.TryWrite(snapshot);
                }

                runtime.StateChanged += OnStateChanged;
                try
                {
                    await WriteSnapshotAsync(http, runtime.GetSnapshot(workbenchId)).ConfigureAwait(false);
                    await foreach (var snapshot in snapshots.Reader.ReadAllAsync(http.RequestAborted))
                        await WriteSnapshotAsync(http, snapshot).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
                {
                    // A disconnected browser is a normal stream termination.
                }
                finally
                {
                    runtime.StateChanged -= OnStateChanged;
                    snapshots.Writer.TryComplete();
                }
            });
        return app;
    }

    private static async Task WriteSnapshotAsync(HttpContext http, WorkbenchRuntimeSnapshot snapshot)
    {
        var payload = JsonSerializer.Serialize(new
        {
            kind = "runtime-state",
            revision = snapshot.WorkbenchRevision,
            timestamp = snapshot.ObservedAt,
            snapshot,
        });
        await http.Response.WriteAsync("event: runtime-state\n", http.RequestAborted).ConfigureAwait(false);
        await http.Response.WriteAsync($"data: {payload}\n\n", http.RequestAborted).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
    }
}
