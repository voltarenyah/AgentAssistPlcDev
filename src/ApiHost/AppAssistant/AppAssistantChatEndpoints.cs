using System.Text.Json;

namespace ApiHost.AppAssistant;

public static class AppAssistantChatEndpoints
{
    public static IEndpointRouteBuilder MapAppAssistantChatEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(
            "/api/app-assistant/bootstrap",
            (HttpContext http, AppAssistantChatRequest request, WorkbenchApiState state,
                AppAssistantClient client, CancellationToken cancellationToken) =>
                StreamAssistantAsync(http, request, state, client, "bootstrap", cancellationToken));
        app.MapPost(
            "/api/app-assistant/chat",
            (HttpContext http, AppAssistantChatRequest request, WorkbenchApiState state,
                AppAssistantClient client, CancellationToken cancellationToken) =>
                StreamAssistantAsync(http, request, state, client, "chat", cancellationToken));
        return app;
    }

    private static async Task<IResult> StreamAssistantAsync(
        HttpContext http,
        AppAssistantChatRequest request,
        WorkbenchApiState state,
        AppAssistantClient client,
        string operation,
        CancellationToken cancellationToken)
    {
        var workbenchId = state.Selection?.WorkbenchId;
        if (string.IsNullOrWhiteSpace(workbenchId))
            return Results.BadRequest(new { error = "WORKBENCH_SELECTION_REQUIRED" });
        if (string.IsNullOrWhiteSpace(request.Message))
            return Results.BadRequest(new { error = "ASSISTANT_MESSAGE_REQUIRED" });

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await WriteEventAsync(http, "progress", new { message = "Reading current workbench state..." })
            .ConfigureAwait(false);
        try
        {
            var payload = await client.SendAsync(operation, workbenchId, request.Message, cancellationToken)
                .ConfigureAwait(false);
            await WriteEventAsync(http, "state", payload).ConfigureAwait(false);
            var answer = payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("answer", out var answerProperty)
                ? answerProperty.GetString()
                : null;
            await WriteEventAsync(http, "answer", new { answer }).ConfigureAwait(false);
        }
        catch (AppAssistantClientException exception)
        {
            await WriteEventAsync(http, "error", new { error = exception.Code, message = exception.Message })
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

public sealed record AppAssistantChatRequest(string Message);
