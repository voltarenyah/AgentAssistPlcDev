using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Chat;

/// <summary>
/// Thin DeepSeek chat-completions client (OpenAI-compatible, no extra packages).
/// POST {baseUrl}/chat/completions with Bearer auth; supports streaming (SSE) and thinking mode
/// (reasoning_content / reasoning_effort per api-docs.deepseek.com/guides/thinking_mode).
/// 401/403 → <see cref="DeepSeekAuthException"/>.
/// </summary>
public sealed class DeepSeekClient
{
    private readonly HttpClient http;
    private readonly string requestUri;

    public DeepSeekClient(string apiKey, string baseUrl)
        : this(apiKey, baseUrl, new HttpClient { Timeout = TimeSpan.FromSeconds(300) })
    {
    }

    /// <summary>Injectable client for tests.</summary>
    public DeepSeekClient(string apiKey, string baseUrl, HttpClient http)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("DeepSeek API key must not be empty.", nameof(apiKey));
        }

        requestUri = baseUrl.TrimEnd('/') + "/chat/completions";
        this.http = http;
        this.http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>The full request URI (shown in session exports; contains no secrets).</summary>
    public string RequestUri => requestUri;

    /// <summary>Non-streaming completion.</summary>
    public async Task<ChatResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        JsonArray? tools,
        ChatRequestSettings settings,
        CancellationToken cancellationToken = default)
    {
        using var response = await PostAsync(messages, tools, settings, stream: false, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            ThrowForStatus(response.StatusCode, payload);
        }

        return ParseResponse(payload);
    }

    /// <summary>
    /// Streaming completion: invokes <paramref name="onDelta"/> per non-empty content/reasoning
    /// piece as it arrives, then returns the assembled response (tool calls, usage included).
    /// </summary>
    public async Task<ChatResponse> CompleteStreamingAsync(
        IReadOnlyList<ChatMessage> messages,
        JsonArray? tools,
        ChatRequestSettings settings,
        Action<ChatDelta> onDelta,
        CancellationToken cancellationToken = default)
    {
        using var response = await PostAsync(messages, tools, settings, stream: true, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorPayload = await response.Content.ReadAsStringAsync(cancellationToken);
            ThrowForStatus(response.StatusCode, errorPayload);
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var toolCalls = new Dictionary<int, ToolCallAccumulator>();
        string? finishReason = null;
        UsageInfo? usage = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            using var chunk = JsonDocument.Parse(data);
            var root = chunk.RootElement;
            if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                usage = ParseUsage(usageElement);
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            {
                finishReason = finish.GetString();
            }

            if (!choice.TryGetProperty("delta", out var delta))
            {
                continue;
            }

            var reasoningPiece = GetStringOrNull(delta, "reasoning_content");
            var contentPiece = GetStringOrNull(delta, "content");
            if (!string.IsNullOrEmpty(reasoningPiece))
            {
                reasoning.Append(reasoningPiece);
            }

            if (!string.IsNullOrEmpty(contentPiece))
            {
                content.Append(contentPiece);
            }

            if (reasoningPiece?.Length > 0 || contentPiece?.Length > 0)
            {
                onDelta(new ChatDelta(reasoningPiece, contentPiece));
            }

            if (delta.TryGetProperty("tool_calls", out var toolCallDeltas) && toolCallDeltas.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolCallDelta in toolCallDeltas.EnumerateArray())
                {
                    AccumulateToolCall(toolCalls, toolCallDelta);
                }
            }
        }

        return new ChatResponse(
            content.Length > 0 ? content.ToString() : null,
            toolCalls.OrderBy(pair => pair.Key).Select(pair => pair.Value.ToToolCall()).ToArray(),
            finishReason,
            usage,
            reasoning.Length > 0 ? reasoning.ToString() : null);
    }

    private async Task<HttpResponseMessage> PostAsync(
        IReadOnlyList<ChatMessage> messages,
        JsonArray? tools,
        ChatRequestSettings settings,
        bool stream,
        CancellationToken cancellationToken)
    {
        var requestMessages = new JsonArray();
        foreach (var message in messages)
        {
            requestMessages.Add(SerializeMessage(message));
        }

        var body = new JsonObject
        {
            ["model"] = settings.Model,
            ["messages"] = requestMessages,
            ["stream"] = stream,
            ["thinking"] = new JsonObject { ["type"] = settings.ThinkingEnabled ? "enabled" : "disabled" },
        };
        if (settings.ThinkingEnabled)
        {
            body["reasoning_effort"] = settings.ReasoningEffort;
        }
        else
        {
            // Thinking mode ignores temperature/top_p (docs) — only send them when it is disabled.
            body["temperature"] = settings.Temperature;
            body["top_p"] = settings.TopP;
        }

        if (tools is { Count: > 0 })
        {
            body["tools"] = tools;
        }

        if (stream)
        {
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static JsonObject SerializeMessage(ChatMessage message)
    {
        var json = new JsonObject
        {
            ["role"] = message.Role,
            ["content"] = message.Content,
        };
        // Thinking mode: assistant reasoning_content must ride along on tool-call turns (docs) —
        // the API ignores it otherwise, so it is sent whenever present.
        if (message.ReasoningContent != null)
        {
            json["reasoning_content"] = message.ReasoningContent;
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            var toolCalls = new JsonArray();
            foreach (var call in message.ToolCalls)
            {
                toolCalls.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.ArgumentsJson,
                    },
                });
            }

            json["tool_calls"] = toolCalls;
        }

        if (message.ToolCallId != null)
        {
            json["tool_call_id"] = message.ToolCallId;
        }

        return json;
    }

    private static ChatResponse ParseResponse(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var choice = root.GetProperty("choices")[0];
        var message = choice.GetProperty("message");

        var toolCalls = new List<ChatToolCall>();
        if (message.TryGetProperty("tool_calls", out var toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCallsElement.EnumerateArray())
            {
                var function = call.GetProperty("function");
                toolCalls.Add(new ChatToolCall(
                    call.GetProperty("id").GetString() ?? string.Empty,
                    function.GetProperty("name").GetString() ?? string.Empty,
                    function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String
                        ? arguments.GetString() ?? "{}"
                        : "{}"));
            }
        }

        UsageInfo? usage = null;
        if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            usage = ParseUsage(usageElement);
        }

        var finishReason = choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String
            ? finish.GetString()
            : null;

        return new ChatResponse(
            GetStringOrNull(message, "content"),
            toolCalls,
            finishReason,
            usage,
            GetStringOrNull(message, "reasoning_content"));
    }

    private static UsageInfo ParseUsage(JsonElement usageElement) =>
        new(
            usageElement.TryGetProperty("prompt_tokens", out var prompt) ? prompt.GetInt32() : 0,
            usageElement.TryGetProperty("completion_tokens", out var completion) ? completion.GetInt32() : 0,
            usageElement.TryGetProperty("total_tokens", out var total) ? total.GetInt32() : 0,
            usageElement.TryGetProperty("completion_tokens_details", out var details) &&
            details.TryGetProperty("reasoning_tokens", out var reasoning)
                ? reasoning.GetInt32()
                : 0);

    private static void AccumulateToolCall(Dictionary<int, ToolCallAccumulator> toolCalls, JsonElement toolCallDelta)
    {
        var index = toolCallDelta.TryGetProperty("index", out var indexElement) ? indexElement.GetInt32() : 0;
        if (!toolCalls.TryGetValue(index, out var accumulator))
        {
            accumulator = new ToolCallAccumulator();
            toolCalls[index] = accumulator;
        }

        if (toolCallDelta.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            accumulator.Id = id.GetString();
        }

        if (!toolCallDelta.TryGetProperty("function", out var function))
        {
            return;
        }

        if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            accumulator.Name = name.GetString();
        }

        if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String)
        {
            accumulator.Arguments.Append(arguments.GetString());
        }
    }

    private static string? GetStringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void ThrowForStatus(HttpStatusCode status, string payload)
    {
        var detail = TryExtractErrorMessage(payload) ?? Truncate(payload, 300);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new DeepSeekAuthException($"DeepSeek rejected the API key ({(int)status}): {detail}");
        }

        throw new DeepSeekException($"DeepSeek request failed ({(int)status} {status}): {detail}");
    }

    private static string? TryExtractErrorMessage(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString();
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";

    private sealed class ToolCallAccumulator
    {
        private readonly StringBuilder arguments = new();

        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Arguments => arguments;

        public ChatToolCall ToToolCall() =>
            new(Id ?? string.Empty, Name ?? string.Empty, arguments.Length > 0 ? arguments.ToString() : "{}");
    }
}

public class DeepSeekException : Exception
{
    public DeepSeekException(string message) : base(message)
    {
    }
}

/// <summary>The API key was rejected (401/403) — the UI should offer the key setup again.</summary>
public sealed class DeepSeekAuthException : DeepSeekException
{
    public DeepSeekAuthException(string message) : base(message)
    {
    }
}
