using System.Text.Json;

namespace ApiHost.AppAssistant;

/// <summary>
/// The Workbench Assistant panel's HTTP surface. Backed in-process by
/// <see cref="WorkbenchAssistantService"/> over the shared <c>AgentLoop</c> (ADR-0005); the Python
/// LangGraph sidecar that used to answer these routes is gone.
/// </summary>
/// <remarks>
/// The response is a buffered, SSE-framed body — the panel reads it with <c>response.text()</c>.
/// That is why a destructive tool's approval cannot be delivered as an <c>interrupt</c> frame: the
/// turn is suspended awaiting the approval, so the body does not end until the user has already
/// decided. Approvals ride the same <c>/api/logs</c> confirmation card and
/// <c>POST /api/chat/confirm/{id}</c> as the device chat, and the <c>state</c> frame carries the
/// panel's session id so the UI can tell its own confirmations apart.
/// </remarks>
public static class AppAssistantChatEndpoints
{
    public static IEndpointRouteBuilder MapAppAssistantChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/app-assistant/health",
            (CompatibilityRuntimeState runtime, IConfiguration configuration) =>
                Results.Ok(new
                {
                    status = "ok",
                    service = "in-process",
                    modelConfigured = !string.IsNullOrWhiteSpace(
                        CompatibilityEndpoints.ResolveApiKey(runtime, configuration)),
                }));
        app.MapPost(
            "/api/app-assistant/bootstrap",
            (HttpContext http, AppAssistantChatRequest request, WorkbenchApiState state,
                WorkbenchAssistantService assistant, CancellationToken cancellationToken) =>
                StreamAssistantAsync(http, request, state, assistant, "bootstrap", cancellationToken));
        app.MapPost(
            "/api/app-assistant/chat",
            (HttpContext http, AppAssistantChatRequest request, WorkbenchApiState state,
                WorkbenchAssistantService assistant, CancellationToken cancellationToken) =>
                StreamAssistantAsync(http, request, state, assistant, "chat", cancellationToken));
        return app;
    }

    private static async Task<IResult> StreamAssistantAsync(
        HttpContext http,
        AppAssistantChatRequest request,
        WorkbenchApiState state,
        WorkbenchAssistantService assistant,
        string operation,
        CancellationToken cancellationToken)
    {
        var workbenchId = state.Selection?.WorkbenchId;
        if (string.IsNullOrWhiteSpace(workbenchId))
            return Results.BadRequest(new { error = "WORKBENCH_SELECTION_REQUIRED" });
        if (operation == "chat" && string.IsNullOrWhiteSpace(request.Message))
            return Results.BadRequest(new { error = "ASSISTANT_MESSAGE_REQUIRED" });

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await WriteEventAsync(http, "progress", new { message = "Reading current workbench state..." })
            .ConfigureAwait(false);
        try
        {
            var turn = operation == "bootstrap"
                ? await assistant.BootstrapAsync(workbenchId, cancellationToken).ConfigureAwait(false)
                : await assistant.ChatAsync(
                    workbenchId,
                    request.Message,
                    // Progress lines are queued by the loop but the panel reads the body only once
                    // the turn ends, so relaying them here would change nothing the user can see.
                    _ => { },
                    cancellationToken).ConfigureAwait(false);
            await WriteEventAsync(http, "state", new
            {
                runtimeSnapshot = turn.Context,
                contextRevision = turn.Context.Runtime.WorkbenchRevision,
                sessionId = turn.SessionId,
            }).ConfigureAwait(false);
            await WriteEventAsync(http, "answer", new { answer = turn.Answer }).ConfigureAwait(false);
        }
        catch (AppAssistantGatewayException exception)
        {
            await WriteEventAsync(http, "error", new { error = exception.Code, message = exception.Message })
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteEventAsync(http, "error", new { error = "APP_ASSISTANT_FAILED", message = exception.Message })
                .ConfigureAwait(false);
        }

        return Results.Empty;
    }

    private static async Task WriteEventAsync(HttpContext http, string eventName, object payload)
    {
        await http.Response.WriteAsync($"event: {eventName}\n", http.RequestAborted).ConfigureAwait(false);
        await http.Response.WriteAsync(
            $"data: {JsonSerializer.Serialize(payload)}\n\n",
            http.RequestAborted).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
    }
}

public sealed record AppAssistantChatRequest(string Message, string? SessionId = null);
