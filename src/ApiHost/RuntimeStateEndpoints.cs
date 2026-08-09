using Agent.Workbench;

public static class RuntimeStateEndpoints
{
    public static IEndpointRouteBuilder MapRuntimeStateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/workbenches/{workbenchId}/runtime-state",
            (string workbenchId, WorkbenchApiState state, WorkbenchRuntimeStateCoordinator runtime) =>
            {
                state.Workbench(workbenchId);
                return Results.Ok(runtime.GetSnapshot(workbenchId));
            });

        app.MapGet(
            "/api/workbenches/{workbenchId}/runtime-state/revision",
            (string workbenchId, WorkbenchApiState state, WorkbenchRuntimeStateCoordinator runtime) =>
            {
                state.Workbench(workbenchId);
                var snapshot = runtime.GetSnapshot(workbenchId);
                return Results.Ok(new
                {
                    workbenchId,
                    revision = snapshot.WorkbenchRevision,
                    observedAt = snapshot.ObservedAt,
                });
            });

        return app;
    }
}
