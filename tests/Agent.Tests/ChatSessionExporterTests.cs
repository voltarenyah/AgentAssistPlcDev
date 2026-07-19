using System.Text.Json;
using Agent.Chat;
using Xunit;

namespace Agent.Tests;

public sealed class ChatSessionExporterTests
{
    [Fact]
    public void ExportContainsAllSectionsInOrder()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.System("SYS-PROMPT with runtime context"),
            ChatMessage.User("hello deepseek"),
            ChatMessage.Assistant("Hi! How can I help?"),
            ChatMessage.User("list sessions"),
            ChatMessage.Assistant(null, new[] { new ChatToolCall("call_1", "list_sessions", "{}") }),
            ChatMessage.Tool("call_1", """{"sessions":[]}"""),
            ChatMessage.Assistant("No sessions running."),
        };
        var usages = new UsageInfo?[]
        {
            new(100, 10, 110),
            new(200, 20, 220),
            new(300, 30, 330),
        };
        var catalog = new McpToolCatalog(new[]
        {
            new AgentToolSpec("list_sessions", "list TIA", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement, new FakeToolCaller()),
        });

        var markdown = ChatSessionExporter.Export(
            history, usages, catalog.ToOpenAiToolsJson(), catalog.Tools.Count, "deepseek-chat", "https://api.deepseek.com/chat/completions");

        Assert.Contains("# Chat session export", markdown);
        Assert.Contains("Model: `deepseek-chat`", markdown);
        Assert.Contains("2 user message(s) · 3 API round(s)", markdown);
        Assert.Contains("600 prompt + 60 completion = 660 total", markdown);

        Assert.Contains("SYS-PROMPT with runtime context", markdown);
        Assert.Contains("## Tool definitions sent with every request (1)", markdown);
        Assert.Contains("list_sessions", markdown);

        Assert.Contains("hello deepseek", markdown);
        Assert.Contains("*usage: 100 prompt + 10 completion = 110 tokens*", markdown);
        Assert.Contains("Requested tool `list_sessions` (call id `call_1`)", markdown);
        Assert.Contains("tool result (call id `call_1`)", markdown);
        Assert.Contains("\"sessions\"", markdown);
        Assert.Contains("*usage: 300 prompt + 30 completion = 330 tokens*", markdown);
        Assert.Contains("No sessions running.", markdown);

        // Conversation order: system prompt section precedes conversation; user 1 before assistant 3.
        Assert.True(markdown.IndexOf("## System prompt", StringComparison.Ordinal) < markdown.IndexOf("## Conversation", StringComparison.Ordinal));
        Assert.True(markdown.IndexOf("hello deepseek", StringComparison.Ordinal) < markdown.IndexOf("No sessions running.", StringComparison.Ordinal));
    }

    [Fact]
    public void PayloadsContainingFencesAreWrappedInTildes()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            ChatMessage.User("show me"),
            ChatMessage.Assistant("here: ```\nfoo\n```"),
        };
        var usages = new UsageInfo?[] { new(1, 1, 2) };

        var markdown = ChatSessionExporter.Export(
            history, usages, new McpToolCatalog(System.Array.Empty<AgentToolSpec>()).ToOpenAiToolsJson(), 0, "m", "http://x");

        // The raw triple-backtick payload must not break the section fences around it.
        Assert.Contains("here:", markdown);
        Assert.DoesNotContain("````", markdown);
    }

    [Fact]
    public void ResolveExportPathCreatesDirectoryUnderLocalAppData()
    {
        var path = ChatSessionExporter.ResolveExportPath();

        Assert.True(Directory.Exists(Path.GetDirectoryName(path)!));
        Assert.EndsWith(Path.Combine("PlcAiAssistant", "chat-exports", Path.GetFileName(path)), path);
        Assert.StartsWith("chat-", Path.GetFileName(path));
        Assert.EndsWith(".md", path);

        // Same write the ExportChat command performs.
        File.WriteAllText(path, "# probe");
        try
        {
            Assert.True(File.Exists(path));
            Assert.Equal("# probe", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportFromRealLoopRunPairsUsagesAndToolCalls()
    {
        var endpoint = new FakeHttpEndpoint();
        endpoint
            .RespondJson(FakeHttpEndpoint.Sse(
                FakeHttpEndpoint.DeltaChunk(null, "I should search first."),
                FakeHttpEndpoint.ToolCallChunk("call_1", "search", """{"text":"Curent_Step"}"""),
                FakeHttpEndpoint.FinalChunk("tool_calls", 50, 5, 3)))
            .RespondJson(FakeHttpEndpoint.Sse(
                FakeHttpEndpoint.DeltaChunk("Network 12 steps the sequencer."),
                FakeHttpEndpoint.FinalChunk("stop", 80, 9)));
        var caller = new FakeToolCaller()
            .Respond("search", JsonDocument.Parse("""{"matches":[{"id":"network:000_Main_PC:12"}]}""").RootElement);
        var catalog = new McpToolCatalog(new[]
        {
            new AgentToolSpec("search", "find text", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement, caller),
        });
        var client = new DeepSeekClient("sk-test", "https://api.deepseek.com", new HttpClient(endpoint));
        var loop = new AgentLoop(client, catalog, () => "CTX dbPath=C:\\x.db");

        await loop.RunAsync("what does network 12 do?");

        var markdown = ChatSessionExporter.Export(
            loop.History, loop.RoundUsages, catalog.ToOpenAiToolsJson(), catalog.Tools.Count, loop.Settings.Model, client.RequestUri);

        Assert.Contains("CTX dbPath=C:\\x.db", markdown);
        Assert.Contains("what does network 12 do?", markdown);
        Assert.Contains("Curent_Step", markdown);                       // tool call arguments
        Assert.Contains("network:000_Main_PC:12", markdown);            // tool result as sent
        Assert.Contains("I should search first.", markdown);            // reasoning_content section
        Assert.Contains("*usage: 50 prompt + 5 completion = 55 tokens (3 reasoning)*", markdown);
        Assert.Contains("*usage: 80 prompt + 9 completion = 89 tokens*", markdown);
        Assert.Contains("130 prompt + 14 completion = 144 total (3 reasoning)", markdown);
        Assert.Equal(loop.History.Count(message => message.Role == "assistant"), loop.RoundUsages.Count);
    }
}
