using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Chat;
using Xunit;

namespace Agent.Tests;

public sealed class AgentLoopTests
{
    private const string ContextMarker = "TEST CONTEXT dbPath=C:\\exports\\TestPLC\\plc-knowledge.db";

    private static string SseText(string text, string? reasoning = null, int promptTokens = 10, int completionTokens = 5, int reasoningTokens = 0, int cacheHitTokens = 0, int cacheMissTokens = 0)
    {
        var chunks = new List<string>();
        if (reasoning != null)
        {
            chunks.Add(FakeHttpEndpoint.DeltaChunk(null, reasoning));
        }

        chunks.Add(FakeHttpEndpoint.DeltaChunk(text));
        chunks.Add(FakeHttpEndpoint.FinalChunk("stop", promptTokens, completionTokens, reasoningTokens, cacheHitTokens, cacheMissTokens));
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

    private static (AgentLoop Loop, FakeHttpEndpoint Endpoint, FakeToolCaller Caller, List<string> Progress, List<(string Kind, string Text)> Deltas) Create() =>
        Create(() => ContextMarker);

    private static (AgentLoop Loop, FakeHttpEndpoint Endpoint, FakeToolCaller Caller, List<string> Progress, List<(string Kind, string Text)> Deltas) Create(Func<string> contextProvider)
    {
        var endpoint = new FakeHttpEndpoint();
        var caller = new FakeToolCaller();
        var catalog = new McpToolCatalog(new[]
        {
            new AgentToolSpec("search", "find text", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement, caller, "test"),
        });
        var client = new DeepSeekClient("sk-test", "https://api.deepseek.com", new HttpClient(endpoint));
        var progress = new List<string>();
        var deltas = new List<(string, string)>();
        var loop = new AgentLoop(client, catalog, contextProvider);
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

        // Static system prompt leads; the runtime context follows as a marked user message.
        var firstRequest = JsonNode.Parse(endpoint.RequestBodies[0])!["messages"]!;
        Assert.Equal("system", firstRequest[0]!["role"]!.GetValue<string>());
        Assert.DoesNotContain(ContextMarker, firstRequest[0]!["content"]!.GetValue<string>());
        var contextMessage = firstRequest[1]!;
        Assert.Equal("user", contextMessage["role"]!.GetValue<string>());
        Assert.StartsWith(SystemPrompt.ContextMessageMarker, contextMessage["content"]!.GetValue<string>());
        Assert.Contains(ContextMarker, contextMessage["content"]!.GetValue<string>());

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
    public async Task RepeatedIdenticalToolErrorDoesNotReachTheCallerAgain()
    {
        var (loop, endpoint, caller, _, _) = Create();
        endpoint
            .RespondJson(SseToolCall("call_1", "search", """{"text":"x"}"""))
            .RespondJson(SseToolCall("call_2", "search", """{"text":"x"}"""))
            .RespondJson(SseText("I cannot find that in the knowledge base."));
        caller.Fail("search", "DB_NOT_FOUND", "Knowledge db not found.");

        await loop.RunAsync("find x");

        Assert.Single(caller.Calls);
        var secondToolMessage = Last(JsonNode.Parse(endpoint.RequestBodies[2])!["messages"]!);
        Assert.Contains("REPEATED_TOOL_ERROR", secondToolMessage["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task InvalidRequiredArgumentsAreRejectedBeforeCaller()
    {
        var endpoint = new FakeHttpEndpoint();
        var caller = new FakeToolCaller();
        var schema = JsonDocument.Parse("""{"type":"object","required":["text"],"properties":{"text":{"type":"string"}}}""");
        var catalog = new McpToolCatalog(new[]
        {
            new AgentToolSpec("search", "find text", schema.RootElement, caller, "test"),
        });
        var client = new DeepSeekClient("sk-test", "https://api.deepseek.com", new HttpClient(endpoint));
        var loop = new AgentLoop(client, catalog, () => ContextMarker);
        endpoint
            .RespondJson(SseToolCall("call_1", "search", "{}"))
            .RespondJson(SseText("The required argument was missing."));

        await loop.RunAsync("search");

        Assert.Empty(caller.Calls);
        var toolMessage = Last(JsonNode.Parse(endpoint.RequestBodies[1])!["messages"]!);
        Assert.Contains("TOOL_ARGUMENT_INVALID", toolMessage["content"]!.GetValue<string>());
        Assert.Contains("text", toolMessage["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task CapHitExecutesPendingToolsAndForcesFinalAnswer()
    {
        var (loop, endpoint, caller, progress, _) = Create();
        for (var i = 0; i < AgentLoop.MaxRounds; i++)
        {
            endpoint.RespondJson(SseToolCall($"call_{i}", "search", "{}"));
            caller.Respond("search", JsonDocument.Parse("{}").RootElement);
        }

        // One final response for the forced no-tools call (with cache metrics for the usage line).
        endpoint.RespondJson(SseText("Final answer from gathered evidence.", cacheHitTokens: 64, cacheMissTokens: 16));

        var answer = await loop.RunAsync("loop forever");

        Assert.Equal("Final answer from gathered evidence.", answer);
        Assert.True(loop.LastTurnHitRoundCap);
        Assert.DoesNotContain("Stopped after", answer);
        Assert.DoesNotContain(loop.History, m => m.Content?.Contains("Stopped after") == true);

        // Every round's tool calls — including the capped round's — were executed.
        Assert.Equal(AgentLoop.MaxRounds, caller.Calls.Count);
        Assert.Equal(AgentLoop.MaxRounds + 1, endpoint.RequestBodies.Count);

        // The final request offers no tools and carries the injected final instruction.
        var finalRequest = JsonNode.Parse(endpoint.RequestBodies[^1])!;
        Assert.Null(finalRequest["tools"]);
        var finalMessages = finalRequest["messages"]!.AsArray();
        var instruction = finalMessages[^1]!;
        Assert.Equal("user", instruction["role"]!.GetValue<string>());
        Assert.Contains("Tool budget exhausted", instruction["content"]!.GetValue<string>());

        // No orphan tool calls: each assistant tool_call id is answered by a tool message.
        AssertToolCallPairing(loop.History);

        // Cache metrics surface in the usage line.
        Assert.Contains(progress, line => line.Contains("cache: 64 hit / 16 miss"));

        // The flag resets on the next turn.
        endpoint.RespondJson(SseText("normal answer"));
        var second = await loop.RunAsync("another question");
        Assert.Equal("normal answer", second);
        Assert.False(loop.LastTurnHitRoundCap);
    }

    [Fact]
    public async Task GrantMoreRoundsExtendsTheToolBudget()
    {
        var (loop, endpoint, caller, _, _) = Create();
        loop.RoundLimit = 1;
        loop.GrantMoreRounds(1);
        Assert.Equal(2, loop.RoundLimit);
        endpoint
            .RespondJson(SseToolCall("call_1", "search", "{}"))
            .RespondJson(SseToolCall("call_2", "search", "{}"))
            .RespondJson(SseText("wrapped up"));
        caller
            .Respond("search", JsonDocument.Parse("{}").RootElement)
            .Respond("search", JsonDocument.Parse("{}").RootElement);

        var answer = await loop.RunAsync("keep going");

        // Without the grant the cap would have forced the final call after round 1 (2 requests).
        Assert.Equal("wrapped up", answer);
        Assert.Equal(3, endpoint.RequestBodies.Count);
        Assert.Equal(2, caller.Calls.Count);
        Assert.True(loop.LastTurnHitRoundCap);
    }

    [Fact]
    public async Task PromptBudgetCompactsOldToolResultsKeepingPairing()
    {
        var (loop, endpoint, caller, progress, _) = Create();
        loop.PromptTokenBudget = 50;
        var bigResult = JsonDocument.Parse($$"""{"text":"{{new string('x', 700)}}"}""").RootElement;
        var smallResult = JsonDocument.Parse("""{"ok":true}""").RootElement;
        endpoint
            .RespondJson(SseToolCall("call_1", "search", "{}", promptTokens: 40))
            .RespondJson(SseToolCall("call_2", "search", "{}", promptTokens: 40))
            .RespondJson(SseToolCall("call_3", "search", "{}", promptTokens: 40))
            .RespondJson(SseText("done"));
        caller.Respond("search", bigResult).Respond("search", smallResult).Respond("search", smallResult);

        var answer = await loop.RunAsync("long session");

        Assert.Equal("done", answer);
        Assert.Contains(progress, line => line.Contains("compacted"));

        // By round 4 the cumulative prompt tokens (3 × 40) crossed the budget: round 1's tool
        // result is shrunk to its head, the last two rounds stay intact.
        var requestMessages = JsonNode.Parse(endpoint.RequestBodies[3])!["messages"]!.AsArray();
        var toolMessages = requestMessages.Where(node => node!["role"]!.GetValue<string>() == "tool").ToArray();
        Assert.Equal(3, toolMessages.Length);

        var compacted = toolMessages[0]!;
        Assert.Equal("tool", compacted["role"]!.GetValue<string>());
        Assert.Equal("call_1", compacted["tool_call_id"]!.GetValue<string>());
        var compactedContent = compacted["content"]!.GetValue<string>();
        var compactedJson = JsonNode.Parse(compactedContent)!.AsObject();
        Assert.True(compactedJson["_truncated"]!.GetValue<bool>());
        Assert.True(compactedContent.Length <= loop.ToolResultCompactChars);

        Assert.Equal(smallResult.GetRawText(), toolMessages[1]!["content"]!.GetValue<string>());
        Assert.Equal(smallResult.GetRawText(), toolMessages[2]!["content"]!.GetValue<string>());

        AssertToolCallPairing(loop.History);
    }

    [Fact]
    public async Task CrossTurnToolResultsCompactedWhenOverThreshold()
    {
        var (loop, endpoint, caller, progress, _) = Create();
        var bigResult = JsonDocument.Parse($$"""{"text":"{{new string('x', 2000)}}"}""").RootElement;
        endpoint
            .RespondJson(SseToolCall("call_1", "search", "{}"))
            .RespondJson(SseText("turn one answer"))
            .RespondJson(SseText("turn two answer"))
            .RespondJson(SseText("turn three answer"));
        caller.Respond("search", bigResult);

        await loop.RunAsync("turn one");
        await loop.RunAsync("turn two");

        // Trigger compaction at the next turn start, with enough margin that stage 1
        // (saves ~1500 chars) brings the estimate back under the threshold on its own.
        loop.RecentTurnsToKeep = 1;
        loop.HistoryTokenThreshold = loop.EstimateNextPromptTokens() - 150;
        await loop.RunAsync("turn three");

        Assert.Contains(progress, line => line.Contains("history compacted") && line.Contains("tool result"));

        // Turn three's request carries turn one's tool result shrunk to its head…
        var requestMessages = JsonNode.Parse(endpoint.RequestBodies[^1])!["messages"]!.AsArray();
        var toolMessage = requestMessages.Single(node => node!["role"]!.GetValue<string>() == "tool")!;
        var compactedContent = toolMessage["content"]!.GetValue<string>();
        var compactedJson = JsonNode.Parse(compactedContent)!.AsObject();
        Assert.True(compactedJson["_truncated"]!.GetValue<bool>());
        Assert.True(compactedContent.Length <= loop.ToolResultCompactChars);

        // …while the kept turn survives intact, and nothing was collapsed (stage 3 not reached).
        Assert.Contains(requestMessages, node => node!["content"]?.GetValue<string>() == "turn two answer");
        Assert.Contains(requestMessages, node => node!["content"]?.GetValue<string>() == "turn one answer");
        AssertToolCallPairing(loop.History);
    }

    [Fact]
    public async Task OldReasoningStrippedAboveThreshold()
    {
        var (loop, endpoint, caller, progress, _) = Create();
        endpoint
            .RespondJson(SseToolCall("call_1", "search", "{}", reasoning: new string('r', 800)))
            .RespondJson(SseText("turn one answer", reasoning: "turn one final thinking"))
            .RespondJson(SseText("turn two answer"))
            .RespondJson(SseText("turn three answer"));
        caller.Respond("search", JsonDocument.Parse("{}").RootElement);

        await loop.RunAsync("turn one");
        await loop.RunAsync("turn two");

        // Stage 2 alone (~820 chars of old reasoning) lands the estimate back under the threshold.
        loop.RecentTurnsToKeep = 1;
        loop.HistoryTokenThreshold = loop.EstimateNextPromptTokens() - 100;
        await loop.RunAsync("turn three");

        Assert.Contains(progress, line => line.Contains("stripped reasoning"));

        // No reasoning_content is replayed for old turns; the turn itself was never collapsed.
        var requestMessages = JsonNode.Parse(endpoint.RequestBodies[^1])!["messages"]!.AsArray();
        Assert.DoesNotContain(requestMessages, node => node!["reasoning_content"] != null);
        Assert.Contains(requestMessages, node => node!["content"]?.GetValue<string>() == "turn one answer");
        AssertToolCallPairing(loop.History);
    }

    [Fact]
    public async Task OldestTurnsCollapsedKeepingPairingAndRecentTurns()
    {
        var (loop, endpoint, caller, progress, _) = Create();
        var bigResult = JsonDocument.Parse($$"""{"text":"{{new string('x', 3000)}}"}""").RootElement;
        endpoint
            .RespondJson(SseToolCall("call_1", "search", "{}"))
            .RespondJson(SseText("turn one answer"))
            .RespondJson(SseToolCall("call_2", "search", "{}"))
            .RespondJson(SseText("turn two answer"))
            .RespondJson(SseText("turn three answer"));
        caller.Respond("search", bigResult).Respond("search", bigResult);

        await loop.RunAsync("turn one");
        await loop.RunAsync("turn two");

        loop.RecentTurnsToKeep = 1;
        loop.HistoryTokenThreshold = 1; // force every stage; collapses everything eligible
        await loop.RunAsync("turn three");

        Assert.Contains(progress, line => line.Contains("collapsed turn"));

        // Turn one collapsed to user + truncated final answer; its tool rounds are gone.
        var history = loop.History;
        Assert.DoesNotContain(history, message => message.ToolCallId == "call_1");
        var collapsed = history.First(message => message.Content?.Contains("turn one answer") == true);
        Assert.Equal("assistant", collapsed.Role);
        Assert.EndsWith("…[earlier tool rounds omitted]", collapsed.Content);
        Assert.Null(collapsed.ReasoningContent);

        // The keep window (turns two and three) stays fully intact.
        Assert.Contains(history, message => message.ToolCallId == "call_2");
        Assert.Contains(history, message => message.Content == "turn two answer");
        Assert.Contains(history, message => message.Content == "turn three answer");

        AssertToolCallPairing(history);

        // Compaction events are counted for the turn snapshot.
        Assert.True(loop.LastTurnCompactions > 0);

        // RoundUsages stays aligned 1:1 with the assistant messages in History.
        Assert.Equal(history.Count(message => message.Role == "assistant"), loop.RoundUsages.Count);
    }

    [Fact]
    public async Task ContextAppendedOnceWhenUnchanged()
    {
        var (loop, endpoint, _, _, _) = Create();
        endpoint.RespondJson(SseText("one")).RespondJson(SseText("two"));

        await loop.RunAsync("first");
        await loop.RunAsync("second");

        Assert.Equal(1, loop.History.Count(SystemPrompt.IsContextMessage));
        var secondRequest = JsonNode.Parse(endpoint.RequestBodies[1])!["messages"]!.AsArray();
        Assert.Equal(
            1,
            secondRequest.Count(node =>
                node!["content"]?.GetValue<string>().StartsWith(SystemPrompt.ContextMessageMarker, StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task ContextChangeAppendsAtTailKeepingPrefixStable()
    {
        var context = "CTX v1";
        var (loop, endpoint, _, progress, _) = Create(() => context);
        endpoint.RespondJson(SseText("answer one")).RespondJson(SseText("answer two"));

        await loop.RunAsync("question one");
        context = "CTX v2 — knowledge now stale";
        await loop.RunAsync("question two");

        var first = JsonNode.Parse(endpoint.RequestBodies[0])!["messages"]!.AsArray();
        var second = JsonNode.Parse(endpoint.RequestBodies[1])!["messages"]!.AsArray();

        // The shared prefix (system + ctx1 + user1 + assistant1) is byte-identical across turns.
        Assert.True(second.Count > first.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i]!.ToJsonString(), second[i]!.ToJsonString());
        }

        // The update rides at the tail, right before the new user message.
        Assert.Equal("user", second[^2]!["role"]!.GetValue<string>());
        Assert.Contains("CTX v2", second[^2]!["content"]!.GetValue<string>());
        Assert.Equal("question two", second[^1]!["content"]!.GetValue<string>());

        // Both context versions are kept frozen in history; the update was narrated.
        Assert.Equal(2, loop.History.Count(SystemPrompt.IsContextMessage));
        Assert.Contains(progress, line => line.Contains("runtime context refreshed"));
    }

    [Fact]
    public async Task ContextMessageNotATurnBoundaryForCompaction()
    {
        var context = "CTX v1";
        var (loop, endpoint, caller, progress, _) = Create(() => context);
        var bigResult = JsonDocument.Parse($$"""{"text":"{{new string('x', 3000)}}"}""").RootElement;
        endpoint
            .RespondJson(SseToolCall("call_1", "search", "{}"))
            .RespondJson(SseText("turn one answer"))
            .RespondJson(SseText("turn two answer"))
            .RespondJson(SseText("turn three answer"));
        caller.Respond("search", bigResult);

        await loop.RunAsync("turn one");
        context = "CTX v2";
        await loop.RunAsync("turn two");

        loop.RecentTurnsToKeep = 1;
        loop.HistoryTokenThreshold = 1; // force every stage
        await loop.RunAsync("turn three");

        // Stage-3 collapse still ran (context messages do not block it as degenerate turns)…
        Assert.Contains(progress, line => line.Contains("collapsed turn"));
        AssertToolCallPairing(loop.History);
        Assert.Contains(loop.History, message => message.Content == "turn three answer");

        // …and the current context message survived inside the keep window.
        Assert.Contains(
            loop.History,
            message => SystemPrompt.IsContextMessage(message) && message.Content!.Contains("CTX v2"));
    }

    [Fact]
    public async Task ContextNotDuplicatedAfterRestore()
    {
        var (loop, endpoint, _, _, _) = Create();
        endpoint.RespondJson(SseText("one")).RespondJson(SseText("two"));

        await loop.RunAsync("first");
        loop.RestoreFrom(loop.History.ToList(), loop.RoundUsages.ToList());
        await loop.RunAsync("second");

        Assert.Equal(1, loop.History.Count(SystemPrompt.IsContextMessage));
    }

    [Fact]
    public void ApplyPolicyUpdatesAllKnobs()
    {
        var (loop, _, _, _, _) = Create();
        loop.Apply(new ChatLoopPolicy
        {
            RoundLimit = 3,
            PromptTokenBudget = 1_000,
            PromptTokenWarningThreshold = 2_000,
            ToolResultMaxChars = 100,
            ToolResultCompactChars = 50,
            HistoryTokenThreshold = 4_000,
            RecentTurnsToKeep = 5,
            CollapsedAnswerChars = 60,
        });

        Assert.Equal(3, loop.RoundLimit);
        Assert.Equal(1_000, loop.PromptTokenBudget);
        Assert.Equal(2_000, loop.PromptTokenWarningThreshold);
        Assert.Equal(100, loop.ToolResultMaxChars);
        Assert.Equal(50, loop.ToolResultCompactChars);
        Assert.Equal(4_000, loop.HistoryTokenThreshold);
        Assert.Equal(5, loop.RecentTurnsToKeep);
        Assert.Equal(60, loop.CollapsedAnswerChars);
    }

    private static void AssertToolCallPairing(IReadOnlyList<ChatMessage> history)
    {
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].ToolCalls is not { Count: > 0 } toolCalls)
            {
                continue;
            }

            Assert.True(i + toolCalls.Count <= history.Count, "assistant tool calls truncated at end of history");
            for (var j = 0; j < toolCalls.Count; j++)
            {
                var reply = history[i + 1 + j];
                Assert.Equal("tool", reply.Role);
                Assert.Equal(toolCalls[j].Id, reply.ToolCallId);
            }
        }
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
        var compacted = JsonNode.Parse(content)!.AsObject();
        Assert.True(compacted["_truncated"]!.GetValue<bool>());
        Assert.True(content.Length <= loop.ToolResultMaxChars);
        Assert.Contains("...", content);
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
