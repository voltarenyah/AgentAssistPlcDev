using System.Text.Json.Nodes;
using Agent.Chat;
using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class SessionManagerTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(
        Path.GetTempPath(),
        "AgentAssistPlcDev.SessionManagerTests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void CreateNewSession_stores_device_identity_under_worktree_automation_directory()
    {
        var device = CreateDeviceContext();
        var settings = new ChatRequestSettings { Model = "test-model" };

        var data = SessionManager.CreateNewSession(device, settings, "runtime context");

        Assert.Equal(device.WorkbenchId, data.Header.WorkbenchId);
        Assert.Equal(device.WorktreeId, data.Header.WorktreeId);
        Assert.Equal(device.DeviceId, data.Header.DeviceId);
        Assert.Equal(device.WorktreeRoot, data.Header.WorktreeRoot);
        Assert.Equal(device.KnowledgeDbPath, data.Header.KnowledgeDbPath);
        Assert.Null(data.Header.ProjectName);
        Assert.Equal("New chat", data.Header.Title);
        Assert.Equal("runtime context", data.Header.RuntimeContext);
        Assert.Empty(data.Messages);
        Assert.Empty(data.RoundUsages);

        var sessionsDirectory = Path.Combine(device.WorktreeRoot, ".automation", "sessions");
        Assert.Equal(sessionsDirectory, SessionManager.SessionsDirectory(device));
        Assert.True(File.Exists(Path.Combine(sessionsDirectory, $"{data.Header.SessionId}.json")));
    }

    [Fact]
    public void SaveSession_and_LoadSession_roundtrip()
    {
        var device = CreateDeviceContext();
        var sessionId = SessionManager.NewSessionId();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var header = new ChatSessionHeader(
            sessionId,
            device.WorkbenchId,
            device.WorktreeId,
            device.DeviceId,
            device.WorktreeRoot,
            device.KnowledgeDbPath,
            now,
            now,
            new ChatRequestSettings { Model = "roundtrip" },
            "ctx");
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

        SessionManager.SaveSession(device, new ChatSessionData(header, messages, usages));
        var loaded = SessionManager.LoadSession(device, sessionId);

        Assert.NotNull(loaded);
        Assert.Equal(sessionId, loaded!.Header.SessionId);
        Assert.Equal(device.WorkbenchId, loaded.Header.WorkbenchId);
        Assert.Equal(device.WorktreeId, loaded.Header.WorktreeId);
        Assert.Equal(device.DeviceId, loaded.Header.DeviceId);
        Assert.Equal(device.WorktreeRoot, loaded.Header.WorktreeRoot);
        Assert.Equal(device.KnowledgeDbPath, loaded.Header.KnowledgeDbPath);
        Assert.Equal("roundtrip", loaded.Header.Settings.Model);
        Assert.Equal(6, loaded.Messages.Count);
        Assert.Equal("hello", loaded.Messages[0].Content);
        Assert.NotNull(loaded.Messages[3].ToolCalls);
        Assert.Equal("get_block", loaded.Messages[3].ToolCalls![0].Name);
        Assert.Equal("c1", loaded.Messages[4].ToolCallId);
        Assert.Equal(3, loaded.RoundUsages.Count);
        Assert.Equal(100, loaded.RoundUsages[0]!.PromptTokens);
    }

    [Fact]
    public void ListSessions_returns_device_identity_and_counts_all_user_turns()
    {
        var device = CreateDeviceContext();
        var settings = new ChatRequestSettings();
        var older = SessionManager.CreateNewSession(device, settings, "older");
        older.Messages.Add(ChatMessage.User("first question"));
        older.Messages.Add(ChatMessage.Assistant("first answer"));
        older.Messages.Add(ChatMessage.User("second question"));
        SessionManager.SaveSession(device, older);
        Thread.Sleep(10);
        var newer = SessionManager.CreateNewSession(device, settings, "newer");

        var sessions = SessionManager.ListSessions(device);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(newer.Header.SessionId, sessions[0].SessionId);
        Assert.Equal(older.Header.SessionId, sessions[1].SessionId);
        Assert.Equal(device.WorkbenchId, sessions[1].WorkbenchId);
        Assert.Equal(device.WorktreeId, sessions[1].WorktreeId);
        Assert.Equal(device.DeviceId, sessions[1].DeviceId);
        Assert.Equal(2, sessions[1].TurnCount);
        Assert.Equal("first question", sessions[1].FirstUserMessage);
    }

    [Fact]
    public void RenameSession_trims_and_persists_title()
    {
        var device = CreateDeviceContext();
        var created = SessionManager.CreateNewSession(device, new ChatRequestSettings(), null);

        var renamed = SessionManager.RenameSession(
            device,
            created.Header.SessionId,
            "  Valve diagnosis  ");

        Assert.NotNull(renamed);
        Assert.Equal("Valve diagnosis", renamed!.Header.Title);
        Assert.Equal(
            "Valve diagnosis",
            SessionManager.LoadSession(device, created.Header.SessionId)!.Header.Title);
    }

    [Fact]
    public void RenameSession_rejects_blank_title()
    {
        var device = CreateDeviceContext();
        var created = SessionManager.CreateNewSession(device, new ChatRequestSettings(), null);

        Assert.Throws<ArgumentException>(() =>
            SessionManager.RenameSession(device, created.Header.SessionId, "   "));
    }

    [Fact]
    public void ListSessions_derives_title_when_stored_title_is_missing()
    {
        var device = CreateDeviceContext();
        var created = SessionManager.CreateNewSession(device, new ChatRequestSettings(), null);
        var untitled = created with
        {
            Header = created.Header with { Title = null },
            Messages = [ChatMessage.User("Investigate the conveyor interlock")],
        };
        SessionManager.SaveSession(device, untitled);

        var info = Assert.Single(SessionManager.ListSessions(device));

        Assert.Equal("Investigate the conveyor interlock", info.Title);
    }

    [Fact]
    public void Missing_and_corrupted_sessions_return_null()
    {
        var device = CreateDeviceContext();
        Assert.Null(SessionManager.LoadSession(device, SessionManager.NewSessionId()));

        var sessionId = SessionManager.NewSessionId();
        Directory.CreateDirectory(SessionManager.SessionsDirectory(device));
        File.WriteAllText(
            Path.Combine(SessionManager.SessionsDirectory(device), $"{sessionId}.json"),
            "not valid json {{{");

        Assert.Null(SessionManager.LoadSession(device, sessionId));
        Assert.Empty(SessionManager.ListSessions(device));

        File.WriteAllText(
            Path.Combine(SessionManager.SessionsDirectory(device), $"{sessionId}.json"),
            "{}");
        Assert.Null(SessionManager.LoadSession(device, sessionId));
    }

    [Fact]
    public void Legacy_project_name_is_deserialized_but_never_used_for_path_resolution()
    {
        var device = CreateDeviceContext();
        var sessionId = SessionManager.NewSessionId();
        var sessionsDirectory = SessionManager.SessionsDirectory(device);
        Directory.CreateDirectory(sessionsDirectory);
        var legacyProjectName = $"legacy-{Guid.NewGuid():N}";
        var json = $$"""
            {
              "header": {
                "sessionId": "{{sessionId}}",
                "projectName": "{{legacyProjectName}}",
                "createdAt": "2026-01-01T00:00:00.0000000+00:00",
                "updatedAt": "2026-01-01T00:00:00.0000000+00:00",
                "settings": { "model": "legacy-model" },
                "runtimeContext": "legacy",
                "exportRoot": "C:\\legacy\\export",
                "knowledgeDbPath": "C:\\legacy\\knowledge.db"
              },
              "messages": [],
              "roundUsages": []
            }
            """;
        File.WriteAllText(Path.Combine(sessionsDirectory, $"{sessionId}.json"), json);

        var loaded = SessionManager.LoadLegacySession(device.WorktreeRoot, sessionId);

        Assert.NotNull(loaded);
        Assert.Equal(legacyProjectName, loaded!.Header.ProjectName);
        Assert.Null(loaded.Header.WorkbenchId);
        Assert.Null(loaded.Header.WorktreeRoot);
        Assert.Throws<InvalidDataException>(() => SessionManager.SaveSession(device, loaded));
        var legacyLocalAppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlcAiAssistant",
            "exports",
            legacyProjectName);
        Assert.False(Directory.Exists(legacyLocalAppDataPath));
    }

    [Fact]
    public void SaveSession_rejects_tampered_context_without_creating_outside_files()
    {
        var device = CreateDeviceContext();
        var data = SessionManager.CreateNewSession(device, new ChatRequestSettings(), null);
        var outsideRoot = Path.Combine(tempRoot, "outside-worktree");
        var tamperedRoot = data with
        {
            Header = data.Header with { WorktreeRoot = outsideRoot },
        };
        var tamperedDatabase = data with
        {
            Header = data.Header with
            {
                KnowledgeDbPath = Path.Combine(tempRoot, "outside", "stolen.db"),
            },
        };

        Assert.Throws<InvalidDataException>(() => SessionManager.SaveSession(device, tamperedRoot));
        Assert.Throws<InvalidDataException>(() => SessionManager.SaveSession(device, tamperedDatabase));
        Assert.Throws<InvalidDataException>(() =>
            SessionManager.SaveSession(
                device,
                data with { Header = data.Header with { WorkbenchId = "wb-other" } }));
        Assert.Throws<InvalidDataException>(() =>
            SessionManager.SaveSession(
                device,
                data with { Header = data.Header with { WorktreeId = "wt-other" } }));
        Assert.Throws<InvalidDataException>(() =>
            SessionManager.SaveSession(
                device,
                data with { Header = data.Header with { DeviceId = "dev-other" } }));
        Assert.False(Directory.Exists(outsideRoot));
        Assert.False(File.Exists(Path.Combine(
            outsideRoot,
            ".automation",
            "sessions",
            $"{data.Header.SessionId}.json")));
        Assert.False(Directory.Exists(Path.Combine(tempRoot, "outside")));
    }

    [Fact]
    public void LoadSession_rejects_a_new_format_header_tampered_to_another_context()
    {
        var device = CreateDeviceContext();
        var data = SessionManager.CreateNewSession(device, new ChatRequestSettings(), null);
        var filePath = SessionManager.ResolveSessionPath(device, data.Header.SessionId)!;
        var outsideRoot = Path.Combine(tempRoot, "outside-worktree");
        var json = JsonNode.Parse(File.ReadAllText(filePath))!;
        json["header"]!["deviceId"] = "dev-other";
        json["header"]!["worktreeRoot"] = outsideRoot;
        File.WriteAllText(filePath, json.ToJsonString());

        var loaded = SessionManager.LoadSession(device, data.Header.SessionId);

        Assert.Null(loaded);
        Assert.False(Directory.Exists(outsideRoot));
        Assert.False(File.Exists(Path.Combine(
            outsideRoot,
            ".automation",
            "sessions",
            $"{data.Header.SessionId}.json")));
    }

    [Fact]
    public void DeleteSession_is_idempotent_and_removes_existing_file()
    {
        var device = CreateDeviceContext();
        var data = SessionManager.CreateNewSession(device, new ChatRequestSettings(), null);
        var filePath = SessionManager.ResolveSessionPath(device, data.Header.SessionId);
        Assert.NotNull(filePath);

        SessionManager.DeleteSession(device, data.Header.SessionId);
        SessionManager.DeleteSession(device, data.Header.SessionId);

        Assert.False(File.Exists(filePath));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("nested/session")]
    [InlineData(".")]
    [InlineData("")]
    public void Session_id_cannot_escape_sessions_directory(string unsafeSessionId)
    {
        var device = CreateDeviceContext();

        Assert.Null(SessionManager.LoadSession(device, unsafeSessionId));
        Assert.Null(SessionManager.ResolveSessionPath(device, unsafeSessionId));
        SessionManager.DeleteSession(device, unsafeSessionId);
    }

    [Fact]
    public void Explicit_identity_creation_rejects_a_relative_worktree_root()
    {
        Assert.Throws<ArgumentException>(() =>
            SessionManager.CreateNewSession(
                "wb-1",
                "wt-1",
                "dev-1",
                "legacy-project-name",
                Path.Combine(tempRoot, "plc-knowledge.db"),
                new ChatRequestSettings(),
                null));
    }

    [Fact]
    public void BuildRuntimeContext_describes_selected_device_and_knowledge_state()
    {
        var device = CreateDeviceContext();

        var runtimeContext = SessionManager.BuildRuntimeContext(
            device,
            "Packaging Line",
            "Valve tuning",
            "feature/valves",
            "PLC_1",
            knowledgeStale: true);

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                $"Workbench: Packaging Line ({device.WorkbenchId})",
                $"Worktree: Valve tuning [feature/valves]",
                $"Device: PLC_1 ({device.DeviceId})",
                $"Exported source: {device.ExportedSourceRoot}",
                $"Modified source: {device.ModifiedSourceRoot}",
                $"Knowledge DB: {device.KnowledgeDbPath}",
                "Knowledge state: stale; run update_components before reuse",
                "Source files: (none — refresh the device export first)"),
            runtimeContext);
    }

    [Fact]
    public void NewSessionId_is_unique_safe_guid_text()
    {
        var ids = Enumerable.Range(0, 100).Select(_ => SessionManager.NewSessionId()).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id =>
        {
            Assert.Equal(32, id.Length);
            Assert.True(Guid.TryParseExact(id, "N", out _));
        });
    }

    private DeviceContext CreateDeviceContext()
    {
        var workbenchRoot = Path.Combine(tempRoot, "workbench");
        var worktreeRoot = Path.Combine(workbenchRoot, "worktrees", "feature-a");
        var deviceRoot = Path.Combine(worktreeRoot, "devices", "PLC_1");
        return new DeviceContext(
            "wb-1",
            "wt-1",
            "dev-1",
            workbenchRoot,
            worktreeRoot,
            deviceRoot,
            Path.Combine(deviceRoot, "exported-source"),
            Path.Combine(deviceRoot, "modified-source"),
            Path.Combine(deviceRoot, "staging"),
            Path.Combine(deviceRoot, "plc-knowledge.db"));
    }
}
