using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Chat;
using Xunit;

namespace Agent.Tests;

public sealed class AgentLoopTests
{
    private const string ContextMarker = "TEST CONTEXT dbPath=C:\\exports\\TestPLC\\plc-knowledge.db";

    private static string SseText(string text, string? reasoning = null, int promptTokens = 10, int completionTokens = 5, int reasoningTokens = 0)
    {
        var chunks = new List<string>();
        if (reasoning != null)
        {
            chunks.Add(FakeHttpEndpoint.DeltaChunk(null, reasoning));
        }

        chunks.Add(FakeHttpEndpoint.DeltaChunk(text));
        chunks.Add(FakeHttpEndpoint.FinalChunk("stop", promptTokens, completionTokens, reasoningTokens));
        return FakeHttpEndpoint.Sse(chunks.ToArray());
    }

    private static string SseToolCall(string id, string name, string arguments, string? reasoning = null, int promptTokens = 10, int completionTokens = 5, int reasoningTokens = 0)
    {
        var chunks = new List<string>();
        if (reasoning != null)
        {
            chunks.Add(FakeHttpEndpoint.DeltaChunk(null, reasoning));
        }

        chunks.Add(FakeHttpEndpoint.ToolCallChunk(id, name, arguments));
        chunks.Add(FakeHttpEndpoint.FinalChunk("tool_calls", promptTokens, completionTokens, reasoningTokens));
        return FakeHttpEndpoint.Sse(chunks.ToArray());
    }

    private static (AgentLoop Loop, FakeHttpEndpoint Endpoint, FakeToolCaller Caller, List<string> Progress, List<(string Kind, string Text)> Deltas) Create()
    {
        var endpoint = new FakeHttpEndpoint();
        var caller = new FakeToolCaller();
        var catalog = new McpToolCatalog(new[]
        {
            new AgentToolSpec("search", "find text", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement, caller),
        });
        var client = new DeepSeekClient("sk-test", "https://api.deepseek.com", new HttpClient(endpoint));
        var progress = new List<string>();
        var deltas = new List<(string, string)>();
        var loop = new AgentLoop(client, catalog, () => ContextMarker);
        loop.Progress += progress.Add;
        loop.StreamDelta += (kind, text) => deltas.Add((kind, text));
        return (loop, endpoint, caller, progress, deltas);
    }

    private static JsonNode Last(JsonNode messages)
    {
        var array = messages.AsArray();
        return array[array.Count - 1]!;
    }

    [Fact]
    public async Task ToolCallRoundTrip()
    {
        var (loop, endpoint, caller, progress, deltas) = Create();
        endpoint
            .RespondJson(SseToolCall("call_1", "search", """{"text":"Curent_Step"}"""))
            .RespondJson(SseText("Network 12 steps the sequencer."));
        caller.Respond("search", JsonDocument.Parse("""{"matches":[{"id":"network:000_Main_PC:12"}]}""").RootElement);

        var answer = await loop.RunAsync("what does network 12 do?");

        Assert.Equal("Network 12 steps the sequencer.", answer);
        Assert.Equal(new[] { "search" }, caller.Calls.ToArray());
        var args = Assert.IsType<JsonElement>(caller.CallArgs["search"][0]);
        Assert.Equal("Curent_Step", args.GetProperty("text").GetString());

        // The second HTTP request carries the tool result back to the model.
        var secondRequest = JsonNode.Parse(endpoint.RequestBodies[1])!["messages"]!;
        var toolMessage = Last(secondRequest);
        Assert.Equal("tool", toolMessage["role"]!.GetValue<string>());
        Assert.Equal("call_1", toolMessage["tool_call_id"]!.GetValue<string>());
        Assert.Contains("network:000_Main_PC:12", toolMessage["content"]!.GetValue<string>());

        // System prompt with the runtime context led the first request.
        var firstRequest = JsonNode.Parse(endpoint.RequestBodies[0])!["messages"]!;
        Assert.Equal("system", firstRequest[0]!["role"]!.GetValue<string>());
        Assert.Contains(ContextMarker, firstRequest[0]!["content"]!.GetValue<string>());

        Assert.Contains(progress, line => line.StartsWith("→ search(", StringComparison.Ordinal));
        Assert.Contains(progress, line => line.Contains("usage: 10 prompt + 5 completion tokens"));
        Assert.Contains(deltas, delta => delta.Kind == "content" && delta.Text.Contains("Network 12"));
    }

    [Fact]
    public async Task ReasoningContentIsPassedBackOnToolCallTurns()
    {
        // Thinking mode: the API returns 400 if the assistant reasoning_content is not replayed
        // on tool-call turns (api-docs.deepseek.com/guides/thinking_mode).
        var (loop, endpoint, caller, progress, _) = Create();
        endpoint
            .RespondJson(SseToolCall("call_1", "search", "{}", reasoning: "I should search first.", reasoningTokens: 40))
            .RespondJson(SseText("done.", reasoning: "Now I can answer."));
        caller.Respond("search", JsonDocument.Parse("{}").RootElement);

        await loop.RunAsync("question");

        // Round 2 request must contain the assistant message with its reasoning_content.
        var secondRequest = JsonNode.Parse(endpoint.RequestBodies[1])!["messages"]!;
        var assistant = secondRequest.AsArray().First(node => node!["role"]!.GetValue<string>() == "assistant")!;
        Assert.Equal("I should search first.", assistant["reasoning_content"]!.GetValue<string>());

        // Usage lines surface reasoning tokens.
        Assert.Contains(progress, line => line.Contains("(40 reasoning)"));

        // Reasoning is kept in history for the export.
        Assert.Equal("Now I can answer.", loop.History.Last().ReasoningContent);
    }

    [Fact]
    public async Task ToolErrorBecomesToolContentSoTheModelCanRecover()
    {
        var (loop, endpoint, caller, _, _) = Create();
        endpoint
            .RespondJson(SseToolCall("call_1", "search", """{"text":"x"}"""))
            .RespondJson(SseText("The knowledge base file is missing; please ingest first."));
        caller.Fail("search", "DB_NOT_FOUND", "Knowledge db not found.");

        var answer = await loop.RunAsync("find x");

        Assert.Contains("knowledge base", answer, StringComparison.OrdinalIgnoreCase);
        var toolMessage = Last(JsonNode.Parse(endpoint.RequestBodies[1])!["messages"]!);
        Assert.Contains("DB_NOT_FOUND", toolMessage["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task StopsAfterMaxRounds()
    {
        var (loop, endpoint, caller, _, _) = Create();
        for (var i = 0; i < AgentLoop.MaxRounds; i++)
        {
            endpoint.RespondJson(SseToolCall($"call_{i}", "search", "{}"));
            caller.Respond("search", JsonDocument.Parse("{}").RootElement);
        }

        var answer = await loop.RunAsync("loop forever");

        Assert.Contains($"Stopped after {AgentLoop.MaxRounds}", answer);
        Assert.Equal(AgentLoop.MaxRounds, endpoint.RequestBodies.Count);
        // The final round's tool calls are not executed.
        Assert.Equal(AgentLoop.MaxRounds - 1, caller.Calls.Count);
    }

    [Fact]
    public async Task LongToolResultsAreTruncated()
    {
        var (loop, endpoint, caller, _, _) = Create();
        endpoint
            .RespondJson(SseToolCall("call_1", "search", "{}"))
            .RespondJson(SseText("done"));
        caller.Respond("search", JsonDocument.Parse($$"""{"text":"{{new string('x', 9000)}}"}""").RootElement);

        await loop.RunAsync("big result");

        var toolMessage = Last(JsonNode.Parse(endpoint.RequestBodies[1])!["messages"]!);
        var content = toolMessage["content"]!.GetValue<string>();
        Assert.Equal(AgentLoop.ToolResultMaxChars + 1, content.Length); // +1 for the ellipsis
        Assert.EndsWith("…", content);
    }

    [Fact]
    public async Task UnknownToolFromModelBecomesToolError()
    {
        var (loop, endpoint, _, _, _) = Create();
        endpoint
            .RespondJson(SseToolCall("call_1", "delete_everything", "{}"))
            .RespondJson(SseText("That tool is not available."));

        var answer = await loop.RunAsync("delete everything");

        Assert.Contains("not available", answer, StringComparison.OrdinalIgnoreCase);
        var toolMessage = Last(JsonNode.Parse(endpoint.RequestBodies[1])!["messages"]!);
        Assert.Contains("AGENT_TOOL_ERROR", toolMessage["content"]!.GetValue<string>());
    }
}
