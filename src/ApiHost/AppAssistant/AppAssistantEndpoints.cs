using Agent.Workbench;

namespace ApiHost.AppAssistant;

public static class AppAssistantEndpoints
{
    public static IEndpointRouteBuilder MapAppAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/internal/app-assistant/workbenches/{workbenchId}/context",
            async (HttpContext http, string workbenchId, AppAssistantGateway gateway, AppAssistantAccessPolicy access) =>
                access.Allowed(http)
                    ? Results.Ok(await gateway.GetContextAsync(workbenchId).ConfigureAwait(false))
                    : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapGet(
            "/internal/app-assistant/workbenches/{workbenchId}/worktrees/{worktreeId}/todos",
            async (HttpContext http, string workbenchId, string worktreeId, int? limit,
                AppAssistantGateway gateway, AppAssistantAccessPolicy access) =>
                access.Allowed(http)
                    ? Results.Ok(await gateway.GetTodosAsync(workbenchId, worktreeId, limit).ConfigureAwait(false))
                    : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapGet(
            "/internal/app-assistant/workbenches/{workbenchId}/worktrees/{worktreeId}/history",
            async (HttpContext http, string workbenchId, string worktreeId, int? limit,
                AppAssistantGateway gateway, AppAssistantAccessPolicy access, CancellationToken cancellationToken) =>
                access.Allowed(http)
                    ? Results.Ok(await gateway.GetHistoryAsync(workbenchId, worktreeId, limit, cancellationToken).ConfigureAwait(false))
                    : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapGet(
            "/internal/app-assistant/workbenches/{workbenchId}/worktrees/{worktreeId}/svn",
            async (HttpContext http, string workbenchId, string worktreeId,
                AppAssistantGateway gateway, AppAssistantAccessPolicy access) =>
                access.Allowed(http)
                    ? Results.Ok(await gateway.GetSvnAsync(workbenchId, worktreeId).ConfigureAwait(false))
                    : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapGet(
            "/internal/app-assistant/workbenches/{workbenchId}/actions",
            async (HttpContext http, string workbenchId, AppAssistantGateway gateway, AppAssistantAccessPolicy access) =>
                access.Allowed(http)
                    ? Results.Ok(await gateway.GetActionsAsync(workbenchId).ConfigureAwait(false))
                    : Results.StatusCode(StatusCodes.Status403Forbidden));

        return app;
    }
}

public sealed class AppAssistantAccessPolicy(IConfiguration configuration, IHostEnvironment environment)
{
    public bool Allowed(HttpContext context)
    {
        if (environment.IsEnvironment("Testing"))
            return true;

        var configuredToken = configuration["AppAssistant:InternalToken"];
        if (!string.IsNullOrWhiteSpace(configuredToken)
            && string.Equals(
                context.Request.Headers["X-App-Assistant-Token"].ToString(),
                configuredToken,
                StringComparison.Ordinal))
            return true;

        var remote = context.Connection.RemoteIpAddress;
        return remote is not null && System.Net.IPAddress.IsLoopback(remote);
    }
}
