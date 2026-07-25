using Agent.Chat;
using Xunit;

namespace Agent.Tests;

public sealed class SessionManagerTests : IDisposable
{
    private readonly List<string> createdProjectNames = new();

    public void Dispose()
    {
        foreach (var projectName in createdProjectNames)
        {
            var exportRoot = AssistantPaths.ResolveExportRoot(projectName);
            try { Directory.Delete(exportRoot, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // The session manager stores files under {exportRoot}\sessions\. Each project's
    // export root is %LOCALAPPDATA%\PlcAiAssistant\exports\{projectName}. We can't
    // redirect AssistantPaths without indirection. Since SessionManager is a stateless
    // static class that calls AssistantPaths.ResolveExportRoot, we test with the real
    // export root path, which maps to %LOCALAPPDATA%\PlcAiAssistant\exports\{projectName}.
    // Tests use unique project names to avoid collisions. Tracked project export roots
    // are cleaned up in Dispose().

    private string UniqueProjectName()
    {
        var name = $"test-{Guid.NewGuid():N}";
        createdProjectNames.Add(name);
        return name;
    }

    [Fact]
    public void CreateNewSession_creates_file_and_returns_valid_data()
    {
        var projectName = UniqueProjectName();
        var settings = new ChatRequestSettings { Model = "test-model" };
        var context = "dbPath=C:\\test.db";

        var data = SessionManager.CreateNewSession(projectName, settings, context);

        Assert.NotNull(data);
        Assert.Equal(projectName, data.Header.ProjectName);
        Assert.Equal("test-model", data.Header.Settings.Model);
        Assert.Equal(context, data.Header.RuntimeContext);
        Assert.Empty(data.Messages);
        Assert.Empty(data.RoundUsages);
        Assert.NotNull(data.Header.SessionId);
        Assert.Equal(32, data.Header.SessionId.Length); // Guid "N" = 32 hex chars

        var filePath = Path.Combine(
            SessionManager.SessionsDirectory(projectName),
            $"{data.Header.SessionId}.json");
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void SaveSession_and_LoadSession_roundtrip()
    {
        var projectName = UniqueProjectName();
        var settings = new ChatRequestSettings { Model = "roundtrip" };
        var sessionId = SessionManager.NewSessionId();
        var now = DateTimeOffset.Now.ToString("O");

        var header = new ChatSessionHeader(
            sessionId, projectName, now, now, settings,
            "ctx", AssistantPaths.ResolveExportRoot(projectName),
            AssistantPaths.ResolveKnowledgeDbPath(projectName));

        var messages = new List<ChatMessage>
        {
            ChatMessage.User("hello"),
            ChatMessage.Assistant("hi there"),
            ChatMessage.User("do something"),
            ChatMessage.Assistant(null, new[] { new ChatToolCall("c1", "get_block", "{}") }),
            ChatMessage.Tool("c1", "{\"result\":\"ok\"}"),
            ChatMessage.Assistant("done"),
        };

        var usages = new List<UsageInfo?>
        {
            new(100, 10, 110),
            new(200, 20, 220),
            new(300, 30, 330),
        };

        var data = new ChatSessionData(header, messages, usages);
        SessionManager.SaveSession(data);

        var loaded = SessionManager.LoadSession(projectName, sessionId);
        Assert.NotNull(loaded);
        Assert.Equal(sessionId, loaded!.Header.SessionId);
        Assert.Equal(projectName, loaded.Header.ProjectName);
        Assert.Equal("roundtrip", loaded.Header.Settings.Model);
        Assert.Equal(6, loaded.Messages.Count);
        Assert.Equal("hello", loaded.Messages[0].Content);
        Assert.Equal("user", loaded.Messages[0].Role);
        Assert.Equal("assistant", loaded.Messages[1].Role);
        Assert.Equal("hi there", loaded.Messages[1].Content);
        Assert.NotNull(loaded.Messages[3].ToolCalls);
        Assert.Single(loaded.Messages[3].ToolCalls!);
        Assert.Equal("c1", loaded.Messages[3].ToolCalls![0].Id);
        Assert.Equal("get_block", loaded.Messages[3].ToolCalls![0].Name);
        Assert.Equal("tool", loaded.Messages[4].Role);
        Assert.Equal("c1", loaded.Messages[4].ToolCallId);
        Assert.Equal(3, loaded.RoundUsages.Count);
        Assert.Equal(100, loaded.RoundUsages[0]!.PromptTokens);
    }

    [Fact]
    public void ListSessions_returns_all_sessions_in_newest_first_order()
    {
        var projectName = UniqueProjectName();
        var settings = new ChatRequestSettings();

        var s1 = SessionManager.CreateNewSession(projectName, settings, "ctx1");
        Thread.Sleep(10); // ensure timestamp ordering
        var s2 = SessionManager.CreateNewSession(projectName, settings, "ctx2");

        var list = SessionManager.ListSessions(projectName);

        Assert.Equal(2, list.Count);
        Assert.Equal(s2.Header.SessionId, list[0].SessionId); // newest first
        Assert.Equal(s1.Header.SessionId, list[1].SessionId);
    }

    [Fact]
    public void ListSessions_returns_empty_for_nonexistent_project()
    {
        var list = SessionManager.ListSessions("nonexistent-project-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(list);
    }

    [Fact]
    public void LoadSession_returns_null_for_missing_session()
    {
        var result = SessionManager.LoadSession("no-project-" + Guid.NewGuid().ToString("N"), "no-session-id");
        Assert.Null(result);
    }

    [Fact]
    public void LoadSession_returns_null_for_corrupted_file()
    {
        var projectName = UniqueProjectName();
        var sessionsDir = SessionManager.SessionsDirectory(projectName);
        Directory.CreateDirectory(sessionsDir);
        var sessionId = SessionManager.NewSessionId();
        var filePath = Path.Combine(sessionsDir, $"{sessionId}.json");

        // Write garbage
        File.WriteAllText(filePath, "not valid json {{{");

        var result = SessionManager.LoadSession(projectName, sessionId);
        Assert.Null(result);
    }

    [Fact]
    public void DeleteSession_removes_file()
    {
        var projectName = UniqueProjectName();
        var settings = new ChatRequestSettings();
        var data = SessionManager.CreateNewSession(projectName, settings, null);

        var filePath = Path.Combine(
            SessionManager.SessionsDirectory(projectName),
            $"{data.Header.SessionId}.json");
        Assert.True(File.Exists(filePath));

        SessionManager.DeleteSession(projectName, data.Header.SessionId);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void DeleteSession_is_idempotent()
    {
        SessionManager.DeleteSession("no-such-project", "no-such-session");
        // Should not throw
    }

    [Fact]
    public void NewSessionId_is_unique()
    {
        var ids = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            var id = SessionManager.NewSessionId();
            Assert.False(ids.Contains(id));
            ids.Add(id);
        }
    }

    [Fact]
    public void SessionsDirectory_uses_export_root()
    {
        var dir = SessionManager.SessionsDirectory("TestProject");
        var expected = Path.Combine(AssistantPaths.ResolveExportRoot("TestProject"), "sessions");
        Assert.Equal(expected, dir);
    }
}
