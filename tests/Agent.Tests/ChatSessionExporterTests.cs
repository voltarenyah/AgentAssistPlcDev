using System.Text.Json;
using Agent.Chat;
using Xunit;

namespace Agent.Tests;

public sealed class ChatSessionExporterTests
{
    [Fact]
    public void ExportPersistedRendersStoredSessionWithoutLiveToolCatalog()
    {
        var session = new ChatSessionData(
            new ChatSessionHeader(
                "s1",
                "wb-1",
                "wt-1",
                "dev-1",
                @"C:\worktrees\master",
                @"C:\worktrees\master\devices\PLC_1\plc-knowledge.db",
                "2026-07-30T00:00:00Z",
                "2026-07-30T01:00:00Z",
                new ChatRequestSettings { Model = "deepseek-chat" },
                null,
                "Startup checks"),
            new List<ChatMessage>
            {
                ChatMessage.System("SYS-PROMPT persisted"),
                ChatMessage.User("hello deepseek"),
                ChatMessage.Assistant("Hi! How can I help?"),
            },
            new List<UsageInfo?> { new(100, 10, 110) });

        var markdown = ChatSessionExporter.ExportPersisted(session);

        Assert.Contains("# Chat session export", markdown);
        Assert.Contains("Model: `deepseek-chat`", markdown);
        Assert.Contains("1 user message(s) · 1 API round(s)", markdown);
        Assert.Contains("## System prompt (as saved in the session file)", markdown);
        Assert.Contains("SYS-PROMPT persisted", markdown);
        Assert.Contains("## Tool definitions", markdown);
        Assert.Contains("not persisted in session files", markdown);
        Assert.DoesNotContain("sent with every request", markdown);
        Assert.Contains("hello deepseek", markdown);
        Assert.Contains("*usage: 100 prompt + 10 completion = 110 tokens*", markdown);
        Assert.Contains("Hi! How can I help?", markdown);
    }

    [Fact]
    public void ResolveSessionExportPathSanitizesTitleUnderWorktreeSessionExportFolder()
    {
        var worktreeRoot = Path.Combine(Path.GetTempPath(), $"session-export-{Guid.NewGuid():N}");
        try
        {
            var path = ChatSessionExporter.ResolveSessionExportPath(
                worktreeRoot,
                "Startup: checks / valves?",
                "s1");

            var directory = Path.Combine(Path.GetFullPath(worktreeRoot), "sessionexport");
            Assert.True(Directory.Exists(directory));
            Assert.StartsWith(directory + Path.DirectorySeparatorChar, path);
            Assert.Equal("Startup_ checks _ valves_.md", Path.GetFileName(path));
        }
        finally
        {
            if (Directory.Exists(worktreeRoot))
                Directory.Delete(worktreeRoot, true);
        }
    }

    [Fact]
    public void ResolveSessionExportPathFallsBackToSessionIdForBlankTitle()
    {
        var worktreeRoot = Path.Combine(Path.GetTempPath(), $"session-export-{Guid.NewGuid():N}");
        try
        {
            var path = ChatSessionExporter.ResolveSessionExportPath(worktreeRoot, "   ", "abc123");

            Assert.Equal("abc123.md", Path.GetFileName(path));
        }
        finally
        {
            if (Directory.Exists(worktreeRoot))
                Directory.Delete(worktreeRoot, true);
        }
    }

    [Fact]
    public void ExportContainsAllSectionsInOrder()
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.System("SYS-PROMPT with runtime context"),
            ChatMessage.User(SystemPrompt.ContextMessage("Workbench: wb-1")),
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
            new AgentToolSpec("list_sessions", "list TIA", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement, new FakeToolCaller(), "test"),
        });

        var markdown = ChatSessionExporter.Export(
            history, usages, catalog.ToOpenAiToolsJson(), catalog.Tools.Count, "deepseek-chat", "https://api.deepseek.com/chat/completions");

        Assert.Contains("# Chat session export", markdown);
        Assert.Contains("Model: `deepseek-chat`", markdown);
        Assert.Contains("2 user message(s) · 3 API round(s)", markdown); // context messages are not user turns
        Assert.Contains("600 prompt + 60 completion = 660 total", markdown);

        Assert.Contains("SYS-PROMPT with runtime context", markdown);
        Assert.Contains("## Tool definitions sent with every request (1)", markdown);
        Assert.Contains("list_sessions", markdown);

        Assert.Contains("runtime context —", markdown);
        Assert.Contains("Workbench: wb-1", markdown);

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
    public void ResolveSessionExportPathCreatesWritableFileUnderWorktree()
    {
        var worktreeRoot = Path.Combine(Path.GetTempPath(), $"session-export-{Guid.NewGuid():N}");
        try
        {
            var path = ChatSessionExporter.ResolveSessionExportPath(worktreeRoot, "chat", "s1");

            Assert.True(Directory.Exists(Path.GetDirectoryName(path)!));
            Assert.EndsWith(Path.Combine("sessionexport", "chat.md"), path);

            File.WriteAllText(path, "# probe");
            Assert.True(File.Exists(path));
            Assert.Equal("# probe", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(worktreeRoot))
                Directory.Delete(worktreeRoot, true);
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
            new AgentToolSpec("search", "find text", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement, caller, "test"),
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
