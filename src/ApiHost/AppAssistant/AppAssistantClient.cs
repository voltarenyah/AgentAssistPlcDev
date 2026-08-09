using System.Net.Http.Json;
using System.Text.Json;

namespace ApiHost.AppAssistant;

public sealed class AppAssistantClient(HttpClient httpClient, IConfiguration configuration)
{
    public async Task<JsonElement> SendAsync(
        string operation,
        string workbenchId,
        string message,
        JsonElement? approval = null,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = configuration["AppAssistant:ServiceUrl"] ?? "http://127.0.0.1:8787";
        var path = operation == "bootstrap" ? "bootstrap" : "chat";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}/v1/workbenches/{Uri.EscapeDataString(workbenchId)}/{path}")
        {
            Content = JsonContent.Create(new { message, approval }),
        };
        var token = configuration["AppAssistant:InternalToken"];
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.TryAddWithoutValidation("X-App-Assistant-Token", token);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return payload;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new AppAssistantClientException(
                "APP_ASSISTANT_UNAVAILABLE",
                "The LangGraph app assistant is currently unavailable.",
                exception);
        }
    }

    public async Task SendFeedbackAsync(
        string workbenchId,
        string category,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = configuration["AppAssistant:ServiceUrl"] ?? "http://127.0.0.1:8787";
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}/v1/workbenches/{Uri.EscapeDataString(workbenchId)}/feedback")
        {
            Content = JsonContent.Create(new { category, runId }),
        };
        var token = configuration["AppAssistant:InternalToken"];
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.TryAddWithoutValidation("X-App-Assistant-Token", token);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new AppAssistantClientException(
                "APP_ASSISTANT_FEEDBACK_UNAVAILABLE",
                "The LangGraph app assistant feedback service is currently unavailable.",
                exception);
        }
    }
}

public sealed class AppAssistantClientException(
    string code,
    string message,
    Exception innerException)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
