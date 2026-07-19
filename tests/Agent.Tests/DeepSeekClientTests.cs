using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Chat;
using Xunit;

namespace Agent.Tests;

/// <summary>Scripted HTTP endpoint: queued (status, body) responses, every request recorded.</summary>
internal sealed class FakeHttpEndpoint : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> responses = new();

    public List<string> RequestBodies { get; } = new();

    public string? LastAuthorization { get; private set; }

    public string? LastRequestUri { get; private set; }

    public FakeHttpEndpoint Respond(HttpStatusCode status, string body)
    {
        responses.Enqueue((status, body));
        return this;
    }

    public FakeHttpEndpoint RespondJson(string body) => Respond(HttpStatusCode.OK, body);

    /// <summary>Wraps chunks as a server-sent-events stream (data: lines + [DONE]).</summary>
    public static string Sse(params string[] chunks) =>
        string.Join("\n\n", chunks.Select(chunk => $"data: {chunk}")) + "\n\ndata: [DONE]\n\n";

    public static string DeltaChunk(string? content, string? reasoning = null, string? finishReason = null)
    {
        var delta = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content,
        };
        if (reasoning != null)
        {
            delta["reasoning_content"] = reasoning;
        }

        var chunk = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finishReason }),
            ["usage"] = null,
        };
        return chunk.ToJsonString();
    }

    public static string FinalChunk(string finishReason, int promptTokens, int completionTokens, int reasoningTokens = 0)
    {
        var usage = new JsonObject
        {
            ["prompt_tokens"] = promptTokens,
            ["completion_tokens"] = completionTokens,
            ["total_tokens"] = promptTokens + completionTokens,
        };
        if (reasoningTokens > 0)
        {
            usage["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = reasoningTokens };
        }

        return new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = new JsonObject { ["content"] = string.Empty },
                ["finish_reason"] = finishReason,
            }),
            ["usage"] = usage,
        }.ToJsonString();
    }

    public static string ToolCallChunk(string id, string name, string argumentsJson, string? finishReason = null)
    {
        return new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = new JsonObject
                {
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["index"] = 0,
                        ["id"] = id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = argumentsJson },
                    }),
                },
                ["finish_reason"] = finishReason,
            }),
            ["usage"] = null,
        }.ToJsonString();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        LastAuthorization = request.Headers.Authorization?.ToString();
        RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (responses.Count == 0)
        {
            throw new InvalidOperationException("FakeHttpEndpoint: no scripted response left.");
        }

        var (status, body) = responses.Dequeue();
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}

public sealed class DeepSeekClientTests
{
    private static readonly ChatRequestSettings Settings = new();

    private static DeepSeekClient Client(FakeHttpEndpoint endpoint) =>
        new("sk-test", "https://api.deepseek.com", new HttpClient(endpoint));

    [Fact]
    public async Task SendsModelMessagesToolsThinkingAndAuthHeader()
    {
        var endpoint = new FakeHttpEndpoint().RespondJson("""
            { "choices": [ { "finish_reason": "stop", "message": { "role": "assistant", "content": "hi" } } ],
              "usage": { "prompt_tokens": 10, "completion_tokens": 2, "total_tokens": 12 } }
            """);
        var tools = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = "search", ["description"] = "find", ["parameters"] = new JsonObject { ["type"] = "object" } },
            },
        };

        var response = await Client(endpoint).CompleteAsync(
            new[] { ChatMessage.System("sys"), ChatMessage.User("hello") }, tools, new ChatRequestSettings { Model = "deepseek-v4-pro" });

        Assert.Equal("https://api.deepseek.com/chat/completions", endpoint.LastRequestUri);
        Assert.Equal("Bearer sk-test", endpoint.LastAuthorization);
        var body = JsonNode.Parse(endpoint.RequestBodies[0])!;
        Assert.Equal("deepseek-v4-pro", body["model"]!.GetValue<string>());
        Assert.Equal("sys", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("search", body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("enabled", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("high", body["reasoning_effort"]!.GetValue<string>());
        // Thinking mode: temperature/top_p are not sent.
        Assert.Null(body["temperature"]);
        Assert.Null(body["top_p"]);
        Assert.Equal("hi", response.Content);
        Assert.Empty(response.ToolCalls);
        Assert.Equal(12, response.Usage!.TotalTokens);
    }

    [Fact]
    public async Task SendsTemperatureAndTopPOnlyWhenThinkingDisabled()
    {
        var endpoint = new FakeHttpEndpoint().RespondJson("""
            { "choices": [ { "finish_reason": "stop", "message": { "role": "assistant", "content": "hi" } } ] }
            """);

        await Client(endpoint).CompleteAsync(
            new[] { ChatMessage.User("hello") },
            null,
            new ChatRequestSettings { ThinkingEnabled = false, Temperature = 0.3, TopP = 0.9 });

        var body = JsonNode.Parse(endpoint.RequestBodies[0])!;
        Assert.Equal("disabled", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Null(body["reasoning_effort"]);
        Assert.Equal(0.3, body["temperature"]!.GetValue<double>());
        Assert.Equal(0.9, body["top_p"]!.GetValue<double>());
    }

    [Fact]
    public async Task ParsesToolCalls()
    {
        var endpoint = new FakeHttpEndpoint().RespondJson("""
            { "choices": [ { "finish_reason": "tool_calls",
                "message": { "role": "assistant", "content": null,
                  "tool_calls": [ { "id": "call_1", "type": "function",
                    "function": { "name": "get_block", "arguments": "{\"block\":\"Main\"}" } } ] } } ] }
            """);

        var response = await Client(endpoint).CompleteAsync(new[] { ChatMessage.User("x") }, null, Settings);

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("get_block", call.Name);
        Assert.Equal("{\"block\":\"Main\"}", call.ArgumentsJson);
        Assert.Equal("tool_calls", response.FinishReason);
        Assert.Null(response.Content);
    }

    [Fact]
    public async Task ParsesReasoningContentAndReasoningTokens()
    {
        var endpoint = new FakeHttpEndpoint().RespondJson("""
            { "choices": [ { "finish_reason": "stop",
                "message": { "role": "assistant", "content": "9.11 is greater", "reasoning_content": "compare 9.11 vs 9.8 …" } } ],
              "usage": { "prompt_tokens": 22, "completion_tokens": 268, "total_tokens": 290,
                "completion_tokens_details": { "reasoning_tokens": 264 } } }
            """);

        var response = await Client(endpoint).CompleteAsync(new[] { ChatMessage.User("x") }, null, Settings);

        Assert.Equal("compare 9.11 vs 9.8 …", response.ReasoningContent);
        Assert.Equal(264, response.Usage!.ReasoningTokens);
    }

    [Fact]
    public async Task SerializesAssistantToolCallsReasoningAndToolResults()
    {
        var endpoint = new FakeHttpEndpoint().RespondJson("""
            { "choices": [ { "finish_reason": "stop", "message": { "role": "assistant", "content": "done" } } ] }
            """);
        var history = new[]
        {
            ChatMessage.User("q"),
            ChatMessage.Assistant(null, new[] { new ChatToolCall("call_1", "search", "{\"text\":\"x\"}") }, "reasoning here"),
            ChatMessage.Tool("call_1", "{\"matches\":[]}"),
        };

        await Client(endpoint).CompleteAsync(history, null, Settings);

        var messages = JsonNode.Parse(endpoint.RequestBodies[0])!["messages"]!;
        var assistant = messages[1]!;
        Assert.Equal("call_1", assistant["tool_calls"]![0]!["id"]!.GetValue<string>());
        Assert.Equal("search", assistant["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("reasoning here", assistant["reasoning_content"]!.GetValue<string>());
        var tool = messages[2]!;
        Assert.Equal("tool", tool["role"]!.GetValue<string>());
        Assert.Equal("call_1", tool["tool_call_id"]!.GetValue<string>());
        Assert.Equal("{\"matches\":[]}", tool["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnauthorizedThrowsAuthException()
    {
        var endpoint = new FakeHttpEndpoint().Respond(HttpStatusCode.Unauthorized, """{ "error": { "message": "bad key" } }""");

        var error = await Assert.ThrowsAsync<DeepSeekAuthException>(() =>
            Client(endpoint).CompleteAsync(new[] { ChatMessage.User("x") }, null, Settings));

        Assert.Contains("bad key", error.Message);
    }

    [Fact]
    public async Task ServerErrorThrowsDeepSeekException()
    {
        var endpoint = new FakeHttpEndpoint().Respond(HttpStatusCode.InternalServerError, "boom");

        var error = await Assert.ThrowsAsync<DeepSeekException>(() =>
            Client(endpoint).CompleteAsync(new[] { ChatMessage.User("x") }, null, Settings));

        Assert.Contains("500", error.Message);
        Assert.IsNotType<DeepSeekAuthException>(error);
    }

    [Fact]
    public void EmptyApiKeyRejected()
    {
        Assert.Throws<ArgumentException>(() => new DeepSeekClient(" ", "https://api.deepseek.com"));
    }

    [Fact]
    public async Task BaseUrlTrailingSlashTrimmed()
    {
        var endpoint = new FakeHttpEndpoint().RespondJson("""
            { "choices": [ { "finish_reason": "stop", "message": { "role": "assistant", "content": "ok" } } ] }
            """);

        await new DeepSeekClient("sk-test", "https://api.deepseek.com/", new HttpClient(endpoint))
            .CompleteAsync(new[] { ChatMessage.User("x") }, null, Settings);

        Assert.Equal("https://api.deepseek.com/chat/completions", endpoint.LastRequestUri);
    }

    // --- Streaming ---

    [Fact]
    public async Task StreamingEmitsDeltasAndAssemblesResponse()
    {
        var endpoint = new FakeHttpEndpoint().RespondJson(FakeHttpEndpoint.Sse(
            FakeHttpEndpoint.DeltaChunk(null, "We need"),
            FakeHttpEndpoint.DeltaChunk(null, " to compare."),
            FakeHttpEndpoint.DeltaChunk("9.11", null),
            FakeHttpEndpoint.DeltaChunk(" is greater.", null),
            FakeHttpEndpoint.FinalChunk("stop", 22, 268, 264)));
        var deltas = new List<ChatDelta>();

        var response = await Client(endpoint).CompleteStreamingAsync(
            new[] { ChatMessage.User("which is greater?") }, null, Settings, deltas.Add);

        Assert.Equal("We need to compare.", response.ReasoningContent);
        Assert.Equal("9.11 is greater.", response.Content);
        Assert.Equal("stop", response.FinishReason);
        Assert.Equal(268, response.Usage!.CompletionTokens);
        Assert.Equal(264, response.Usage.ReasoningTokens);
        Assert.Equal(4, deltas.Count);
        Assert.Equal("We need", deltas[0].ReasoningContent);
        Assert.Equal("9.11", deltas[2].Content);
    }

    [Fact]
    public async Task StreamingAccumulatesToolCallsFromFragments()
    {
        var endpoint = new FakeHttpEndpoint().RespondJson(FakeHttpEndpoint.Sse(
            FakeHttpEndpoint.ToolCallChunk("call_1", "get_weather", ""),
            FakeHttpEndpoint.ToolCallChunk("call_1", "get_weather", "{\"loc"),
            FakeHttpEndpoint.ToolCallChunk("call_1", "get_weather", "ation\":\"HZ\"}"),
            FakeHttpEndpoint.FinalChunk("tool_calls", 10, 5)));

        var response = await Client(endpoint).CompleteStreamingAsync(
            new[] { ChatMessage.User("weather?") }, null, Settings, _ => { });

        var call = Assert.Single(response.ToolCalls);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("{\"location\":\"HZ\"}", call.ArgumentsJson);
        Assert.Equal("tool_calls", response.FinishReason);
        Assert.Equal(15, response.Usage!.TotalTokens);
    }

    [Fact]
    public async Task StreamingRequestMarksStreamAndIncludeUsage()
    {
        var endpoint = new FakeHttpEndpoint().RespondJson(FakeHttpEndpoint.Sse(
            FakeHttpEndpoint.DeltaChunk("hi"),
            FakeHttpEndpoint.FinalChunk("stop", 1, 1)));

        await Client(endpoint).CompleteStreamingAsync(new[] { ChatMessage.User("x") }, null, Settings, _ => { });

        var body = JsonNode.Parse(endpoint.RequestBodies[0])!;
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public async Task StreamingHttpErrorThrows()
    {
        var endpoint = new FakeHttpEndpoint().Respond(HttpStatusCode.Unauthorized, """{ "error": { "message": "bad key" } }""");

        await Assert.ThrowsAsync<DeepSeekAuthException>(() =>
            Client(endpoint).CompleteStreamingAsync(new[] { ChatMessage.User("x") }, null, Settings, _ => { }));
    }
}
