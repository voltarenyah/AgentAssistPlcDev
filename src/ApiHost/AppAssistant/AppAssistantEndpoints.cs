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
            async (HttpContext http, string workbenchId, string worktreeId, int? limit, string? depth,
                AppAssistantGateway gateway, AppAssistantAccessPolicy access, CancellationToken cancellationToken) =>
                access.Allowed(http)
                    ? Results.Ok(string.IsNullOrWhiteSpace(depth)
                        ? await gateway.GetHistoryAsync(workbenchId, worktreeId, limit, cancellationToken).ConfigureAwait(false)
                        : await gateway.GetHistoryByDepthAsync(workbenchId, worktreeId, depth, cancellationToken).ConfigureAwait(false))
                    : Results.StatusCode(StatusCodes.Status403Forbidden));

        app.MapGet(
            "/internal/app-assistant/workbenches/{workbenchId}/worktrees/{worktreeId}/svn-history",
            async (HttpContext http, string workbenchId, string worktreeId, string? depth,
                AppAssistantGateway gateway, AppAssistantAccessPolicy access, CancellationToken cancellationToken) =>
                access.Allowed(http)
                    ? Results.Ok(await gateway.GetSvnHistoryByDepthAsync(workbenchId, worktreeId, depth, cancellationToken).ConfigureAwait(false))
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

        app.MapPost(
            "/internal/app-assistant/workbenches/{workbenchId}/mutations/create-worktree",
            async (HttpContext http, string workbenchId, CreateWorktreeAssistantRequest request,
                AppAssistantGateway gateway, AppAssistantAccessPolicy access, CancellationToken cancellationToken) =>
            {
                if (!access.Allowed(http))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                var result = await gateway.CreateWorktreeAsync(workbenchId, request, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(result);
            });

        app.MapPost(
            "/internal/app-assistant/workbenches/{workbenchId}/mutations/create-workbench",
            async (HttpContext http, string workbenchId, CreateWorkbenchAssistantRequest request,
                AppAssistantGateway gateway, AppAssistantAccessPolicy access, CancellationToken cancellationToken) =>
            {
                if (!access.Allowed(http))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                try
                {
                    return Results.Ok(await gateway.CreateWorkbenchAsync(workbenchId, request, cancellationToken)
                        .ConfigureAwait(false));
                }
                catch (AppAssistantGatewayException exception)
                {
                    return Results.Json(
                        new { error = exception.Code, message = exception.Message },
                        statusCode: exception.StatusCode);
                }
                catch (RuntimeStateConflictException exception)
                {
                    return Results.Conflict(new
                    {
                        error = "CONTEXT_STALE",
                        message = exception.Message,
                    });
                }
                catch (WorkbenchLifecycleException exception)
                {
                    return Results.BadRequest(new { error = exception.Code, message = exception.Message });
                }
            });

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
