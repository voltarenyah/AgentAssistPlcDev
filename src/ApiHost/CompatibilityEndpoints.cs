using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Mcp;
using Agent.Chat;
using Agent.Workbench;
using Contracts.Sandbox;
using Microsoft.AspNetCore.Http.Features;

public sealed record CompatibilityToolCallRequest(string Tool, Dictionary<string, object?>? Arguments);
public sealed record CompatibilityPathRequest(string? FilePath, string? Message, string[]? Paths);

public sealed class ApiMcpGateway(
    IMcpToolCaller engineering,
    IMcpToolCaller knowledge,
    IMcpToolCaller versionControl,
    IMcpToolCaller sourceEditor)
{
    private static readonly IReadOnlySet<string> KnowledgeTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "ingest_source", "update_components", "query", "get_schema", "get_block",
        "get_network", "get_single_network", "get_all_networks", "get_variable_usage", "search", "query_node_kinds", "query_nodes", "query_edge_types",
        "query_edges", "query_node_properties", "query_edge_properties",
    };
    public IMcpToolCaller For(string tool)
    {
        if (tool.StartsWith("vc_", StringComparison.Ordinal)) return versionControl;
        if (tool.StartsWith("src_", StringComparison.Ordinal)) return sourceEditor;
        if (KnowledgeTools.Contains(tool)) return knowledge;
        return engineering;
    }
}

public sealed class CompatibilityRuntimeState
{
    private int chatGeneration;
    public ConcurrentQueue<string> Logs { get; } = new();
    public ConcurrentQueue<JsonElement> ChatHistory { get; } = new();
    public string? ApiKey { get; set; }
    public ConcurrentDictionary<string, JsonElement> Connections { get; } = new(StringComparer.Ordinal);
    public string? ActiveConnectionId { get; set; }
    public JsonElement? ChatSettings { get; set; }
    public int ChatGeneration => Volatile.Read(ref chatGeneration);
    public void IncrementChatGeneration() => Interlocked.Increment(ref chatGeneration);
}

public sealed class CompatibilityConfigStore
{
    private readonly string path;
    public CompatibilityConfigStore(string? path = null)
    {
        this.path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AutomationWorkbench", "config.json");
    }
    public void Set(string name, JsonNode? value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonObject root;
        try { root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new() : new(); }
        catch (JsonException) { root = new(); }
        root[name] = value?.DeepClone();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}

public static class CompatibilityEndpoints
{
    public static IEndpointRouteBuilder MapCompatibilityEndpoints(this IEndpointRouteBuilder app)
    {
        static DeviceContext Device(WorkbenchApiState state) =>
            state.Selection?.DeviceId is { } id
                ? state.Device(id).Context
                : throw new InvalidOperationException("DEVICE_SELECTION_REQUIRED");

        static string ExportSessionFile(DeviceContext device, string sessionId, ChatSessionData session)
        {
            var path = ChatSessionExporter.ResolveSessionExportPath(
                device.WorktreeRoot,
                session.Header.Title,
                sessionId);
            File.WriteAllText(path, ChatSessionExporter.ExportPersisted(session));
            return path;
        }

        app.MapPost("/api/connect", async (JsonElement body, ApiMcpGateway gateway, CompatibilityRuntimeState state, CancellationToken ct) =>
        {
            var result = await gateway.For("connect").CallAsync<object>("connect", body, ct);
            var id = body.TryGetProperty("connectionId", out var supplied) ? supplied.GetString() : null;
            id ??= Guid.NewGuid().ToString("N");
            state.Connections[id] = body.Clone();
            state.ActiveConnectionId = id;
            return result;
        });
        app.MapPost("/api/disconnect", async (ApiMcpGateway gateway, CompatibilityRuntimeState state, CancellationToken ct) =>
        {
            var result = await gateway.For("disconnect").CallAsync<object>("disconnect", new { }, ct);
            state.ActiveConnectionId = null;
            return result;
        });
        app.MapGet("/api/connections", (CompatibilityRuntimeState state) => Results.Ok(new
        {
            activeConnectionId = state.ActiveConnectionId,
            connections = state.Connections.Select(pair => new { connectionId = pair.Key, request = pair.Value }),
        }));
        app.MapPost("/api/connections/switch", async (
            HttpContext http,
            JsonElement body,
            ApiMcpGateway gateway,
            CompatibilityRuntimeState state,
            OperationStatusRegistry operations,
            CancellationToken ct) =>
        {
            return await RunOperationAsync(
                http,
                operations,
                "open-project-in-tia",
                "Opening project in TIA Portal...",
                async _ =>
                {
                    JsonElement request;
                    string id;
                    var rememberConnection = false;
                    if (body.TryGetProperty("connectionId", out var suppliedId))
                    {
                        id = suppliedId.GetString() ?? throw new ArgumentException("connectionId is required.");
                        if (!state.Connections.TryGetValue(id, out request))
                            throw new KeyNotFoundException("CONNECTION_NOT_FOUND");
                    }
                    else
                    {
                        var projectPath = body.GetProperty("projectPath").GetString();
                        if (string.IsNullOrWhiteSpace(projectPath))
                            throw new ArgumentException("projectPath is required.");
                        id = Guid.NewGuid().ToString("N");
                        request = body.Clone();
                        rememberConnection = true;
                    }

                    var result = await gateway.For("connect").CallAsync<object>("connect", request, ct);
                    if (rememberConnection)
                        state.Connections[id] = request;
                    state.ActiveConnectionId = id;
                    return result;
                },
                "Project opened in TIA Portal.");
        });
        app.MapGet("/api/tools", async (IServiceProvider services, CancellationToken ct) =>
        {
            var runtime = services.GetService<McpRuntime>();
            if (runtime is null) return Results.Ok(Array.Empty<object>());
            var catalog = await McpToolCatalog.BuildAsync(runtime.Host, ct);
            return Results.Ok(catalog.Tools.Select(tool => new { tool.Name, tool.Description, tool.ServerName }));
        });
        app.MapGet("/api/sessions", async (ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("list_sessions").CallAsync<JsonElement>("list_sessions", new { }, ct));

        app.MapPost("/api/tools/call", async (CompatibilityToolCallRequest request, WorkbenchApiState state, SandboxedToolExecutor executor, CancellationToken ct) =>
        {
            var device = Device(state);
            return await executor.RequestAsync(request.Tool, request.Arguments ?? new(), device, "api", ct);
        });
        app.MapPost("/api/chat/confirm/{id}", async (string id, JsonElement body, WorkbenchApiState state, PendingToolActions pending, ApiChatService chat) =>
        {
            var decision = body.TryGetProperty("decision", out var value) ? value.GetString() : null;
            var parsed = decision switch
            {
                "allowOnce" => ToolConfirmation.AllowOnce,
                // Direct approvals are deliberately one-shot; session grants belong to AgentSandbox.
                "allowSession" => ToolConfirmation.AllowOnce,
                _ => ToolConfirmation.Deny,
            };
            var device = Device(state);
            var requester = pending.Requester(id) ?? throw new KeyNotFoundException("CONFIRMATION_NOT_FOUND");
            if (requester != "api" && requester != chat.ActiveSessionId(device))
                throw new InvalidOperationException("CONFIRMATION_CONTEXT_MISMATCH");
            return Results.Ok(await pending.ResolveAsync(
                id, parsed, DeviceContextIdentity.Key(device), requester));
        });
        app.MapGet("/api/logs", (CompatibilityRuntimeState state) => state.Logs.ToArray());
        app.MapPost("/api/chat/grant-rounds", (JsonElement body, WorkbenchApiState state, ApiChatService chat) =>
        {
            var additional = body.TryGetProperty("additional", out var value) && value.TryGetInt32(out var parsed)
                ? parsed
                : 6;
            return chat.GrantMoreRounds(Device(state), additional) ? Results.NoContent() : Results.NotFound();
        });
        app.MapGet("/api/chat/history", (WorkbenchApiState state, ApiChatService chat) =>
            chat.History(Device(state)));
        app.MapPost("/api/chat/clear", (WorkbenchApiState state, ApiChatService chat) =>
        {
            chat.Clear(Device(state));
            return Results.NoContent();
        });
        app.MapPost("/api/chat", async (HttpContext http, JsonElement body, WorkbenchApiState state, ApiChatService chat, CancellationToken ct) =>
        {
            var device = Device(state);
            var message = body.TryGetProperty("message", out var value) ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("message is required.");
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Append("X-Accel-Buffering", "no");
            http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await http.Response.StartAsync(ct);

            var events = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

            void Queue(object payload) =>
                events.Writer.TryWrite("data: " + JsonSerializer.Serialize(payload) + "\n\n");

            var producer = Task.Run(async () =>
            {
                try
                {
                    Queue(new { kind = "progress", delta = "Preparing chat context..." });
                    var answer = await chat.RunStreamingAsync(
                        device,
                        message,
                        line => Queue(new { kind = "progress", delta = line }),
                        (kind, delta) => Queue(new { kind, delta }),
                        ct);
                    var snapshot = chat.TurnSnapshot(device);
                    Queue(new
                    {
                        kind = "meta",
                        hitRoundCap = snapshot.HitRoundCap,
                        compactions = snapshot.Compactions,
                        usage = snapshot.Usage is { } usage
                            ? new
                            {
                                promptTokens = usage.PromptTokens,
                                completionTokens = usage.CompletionTokens,
                                totalTokens = usage.TotalTokens,
                                promptCacheHitTokens = usage.PromptCacheHitTokens,
                                promptCacheMissTokens = usage.PromptCacheMissTokens,
                            }
                            : null,
                    });
                    Queue(new { kind = "progress", delta = "Saving chat session..." });
                    Queue(new { kind = "answer", delta = answer });
                }
                catch (Exception exception)
                {
                    Queue(new { kind = "error", delta = exception.Message });
                }
                finally
                {
                    events.Writer.TryComplete();
                }
            }, ct);

            await foreach (var item in events.Reader.ReadAllAsync(ct))
            {
                await http.Response.WriteAsync(item, ct);
                await http.Response.Body.FlushAsync(ct);
            }

            await http.Response.WriteAsync("data: [DONE]\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
            await producer;
        });
        app.MapGet("/api/config/key/status", (CompatibilityRuntimeState state, IConfiguration configuration) => Results.Ok(new
        {
            configured = !string.IsNullOrWhiteSpace(state.ApiKey ?? configuration["DeepSeek:ApiKey"] ?? configuration["deepSeekApiKey"]),
        }));
        app.MapPost("/api/config/key", (JsonElement body, CompatibilityRuntimeState state, CompatibilityConfigStore store, ApiChatService chat) =>
        {
            state.ApiKey = body.TryGetProperty("apiKey", out var apiKey)
                ? apiKey.GetString()
                : body.TryGetProperty("key", out var key) ? key.GetString() : null;
            if (string.IsNullOrWhiteSpace(state.ApiKey)) throw new ArgumentException("API key must not be blank.");
            store.Set("deepSeekApiKey", JsonValue.Create(state.ApiKey));
            chat.Reset();
            return Results.NoContent();
        });
        app.MapPost("/api/config/settings", (JsonElement body, CompatibilityRuntimeState state, CompatibilityConfigStore store, ApiChatService chat) =>
        {
            store.Set("chatSettings", JsonNode.Parse(body.GetRawText()));
            state.ChatSettings = body.Clone();
            chat.Reset();
            return Results.NoContent();
        });
        app.MapGet("/api/config/settings", (IConfiguration configuration, CompatibilityRuntimeState state) =>
        {
            var settings = ApiChatService.Settings(configuration, state);
            var policy = ApiChatService.LoopPolicy(configuration, state);
            return Results.Ok(new
            {
                model = settings.Model,
                thinkingEnabled = settings.ThinkingEnabled,
                reasoningEffort = settings.ReasoningEffort,
                temperature = settings.Temperature,
                topP = settings.TopP,
                contextWindow = ApiChatService.ContextWindow(configuration),
                roundLimit = policy.RoundLimit,
                promptTokenBudget = policy.PromptTokenBudget,
                promptTokenWarningThreshold = policy.PromptTokenWarningThreshold,
                toolResultMaxChars = policy.ToolResultMaxChars,
                toolResultCompactChars = policy.ToolResultCompactChars,
                historyTokenThreshold = policy.HistoryTokenThreshold,
                recentTurnsToKeep = policy.RecentTurnsToKeep,
                collapsedAnswerChars = policy.CollapsedAnswerChars,
            });
        });
        app.MapGet("/api/chat/sessions", (WorkbenchApiState state) => SessionManager.ListSessions(Device(state)));
        app.MapPost("/api/chat/session/new", (WorkbenchApiState state, ApiChatService chat) =>
            chat.CreateSession(Device(state)));
        app.MapPost("/api/chat/session/load", (JsonElement body, WorkbenchApiState state, ApiChatService chat) =>
        {
            var id = body.GetProperty("sessionId").GetString() ?? throw new ArgumentException("sessionId is required.");
            return chat.LoadSession(Device(state), id) is { } session ? Results.Ok(session) : Results.NotFound();
        });
        app.MapPost("/api/chat/session/rename", (JsonElement body, WorkbenchApiState state, ApiChatService chat) =>
        {
            var id = body.GetProperty("sessionId").GetString() ?? throw new ArgumentException("sessionId is required.");
            var title = body.GetProperty("title").GetString() ?? throw new ArgumentException("title is required.");
            return chat.RenameSession(Device(state), id, title) is { } session
                ? Results.Ok(session)
                : Results.NotFound();
        });
        app.MapPost("/api/chat/session/delete", (JsonElement body, WorkbenchApiState state, ApiChatService chat) =>
        {
            var id = body.GetProperty("sessionId").GetString() ?? throw new ArgumentException("sessionId is required.");
            chat.DeleteSession(Device(state), id);
            return Results.NoContent();
        });
        app.MapPost("/api/chat/session/export", (JsonElement body, WorkbenchApiState state) =>
        {
            var id = body.GetProperty("sessionId").GetString() ?? throw new ArgumentException("sessionId is required.");
            var device = Device(state);
            return SessionManager.LoadSession(device, id) is { } session
                ? Results.Ok(new { path = ExportSessionFile(device, id, session) })
                : Results.NotFound();
        });
        app.MapGet("/api/chat/session/info", (WorkbenchApiState state, ApiChatService chat) =>
        {
            var device = Device(state);
            return Results.Ok(new
            {
                selection = state.Selection,
                sessions = SessionManager.ListSessions(device).Count,
                activeSessionId = chat.ActiveSessionId(device),
                requiresExplicitSession = chat.RequiresExplicitSession(device),
            });
        });

        app.MapGet("/api/project/info", (WorkbenchApiState state, DeviceSnapshotReader snapshots) =>
        {
            var selected = state.Device(Device(state).DeviceId);
            return Results.Ok(snapshots.Read(selected.Context, selected.Metadata));
        });
        app.MapGet("/api/blocks", (WorkbenchApiState state, DeviceSnapshotReader snapshots) =>
        {
            var selected = state.Device(Device(state).DeviceId);
            return Results.Ok(snapshots.Read(selected.Context, selected.Metadata).Blocks);
        });
        app.MapGet("/api/blocks/{blockName}/source-code", async (string blockName, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("get_block").CallAsync<JsonElement>("get_block", new { dbPath = Device(state).KnowledgeDbPath, blockName }, ct));
        app.MapPost("/api/project/select-plc", (WorkbenchApiState state) => Results.Ok(new { deviceId = Device(state).DeviceId }));
        app.MapGet("/api/project/context-status", (WorkbenchApiState state) =>
        {
            var device = Device(state);
            return Results.Ok(new { device.DeviceId, device.ExportedSourceRoot, device.ModifiedSourceRoot, device.StagingRoot, device.KnowledgeDbPath });
        });
        app.MapGet("/api/project/compare", (WorkbenchApiState state, WorkbenchCoordinator coordinator) =>
            coordinator.PreviewRefresh(Device(state)));
        app.MapGet("/api/check-environment", async (ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("check_environment").CallAsync<JsonElement>("check_environment", new { }, ct));
        app.MapGet("/api/browse", (string? path, WorkbenchApiState state) =>
        {
            var device = Device(state);
            var resolved = string.IsNullOrWhiteSpace(path) ? device.WorktreeRoot : WorkbenchPaths.ResolveRelative(device.WorktreeRoot, path);
            return Results.Ok(new { path = resolved, entries = Directory.Exists(resolved) ? Directory.EnumerateFileSystemEntries(resolved).Select(Path.GetFileName) : [] });
        });
        app.MapGet("/api/knowledge/node-kinds", async (WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("query_node_kinds").CallAsync<JsonElement>("query_node_kinds", new { dbPath = Device(state).KnowledgeDbPath }, ct));
        app.MapGet("/api/knowledge/block-interface", (string blockName, WorkbenchApiState state) =>
            Results.Ok(BlockInterfaceReader.Read(Device(state).KnowledgeDbPath, blockName)));
        app.MapGet("/api/knowledge/nodes", async (string? kind, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("query_nodes").CallAsync<JsonElement>("query_nodes", new { dbPath = Device(state).KnowledgeDbPath, kind }, ct));
        app.MapGet("/api/knowledge/edge-types", async (WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("query_edge_types").CallAsync<JsonElement>("query_edge_types", new { dbPath = Device(state).KnowledgeDbPath }, ct));
        app.MapGet("/api/knowledge/edges", async (string? nodeId, string? type, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("query_edges").CallAsync<JsonElement>("query_edges", new { dbPath = Device(state).KnowledgeDbPath, nodeId, type }, ct));
        app.MapGet("/api/knowledge/node-properties", async (string nodeId, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("query_node_properties").CallAsync<JsonElement>("query_node_properties", new { dbPath = Device(state).KnowledgeDbPath, nodeId }, ct));
        app.MapGet("/api/knowledge/edge-properties", async (string edgeId, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("query_edge_properties").CallAsync<JsonElement>("query_edge_properties", new { dbPath = Device(state).KnowledgeDbPath, edgeId }, ct));

        app.MapGet("/api/vc/status", async (WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_status").CallAsync<JsonElement>("vc_status", new { repoPath = Device(state).WorktreeRoot }, ct));
        app.MapGet("/api/vc/log", async (int? maxCount, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_log").CallAsync<JsonElement>("vc_log", new { repoPath = Device(state).WorktreeRoot, maxCount }, ct));
        app.MapGet("/api/vc/diff", async (string filePath, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_diff").CallAsync<JsonElement>("vc_diff", new { repoPath = Device(state).WorktreeRoot, filePath }, ct));
        app.MapPost("/api/vc/add", async (CompatibilityPathRequest body, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_add").CallAsync<JsonElement>("vc_add", new { repoPath = Device(state).WorktreeRoot, paths = body.Paths ?? [] }, ct));
        app.MapPost("/api/vc/commit", async (CompatibilityPathRequest body, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_commit").CallAsync<JsonElement>("vc_commit", new { repoPath = Device(state).WorktreeRoot, message = body.Message }, ct));
        app.MapPost("/api/vc/restore", async (CompatibilityPathRequest body, WorkbenchApiState state, SandboxedToolExecutor executor, CancellationToken ct) =>
            await executor.RequestAsync("vc_restore", new Dictionary<string, object?> { ["filePath"] = body.FilePath }, Device(state), "api", ct));
        app.MapGet("/api/vc/branches", async (WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_branches").CallAsync<JsonElement>("vc_branches", new { repoPath = Device(state).WorktreeRoot }, ct));
        app.MapPost("/api/vc/checkout", async (JsonElement body, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_checkout").CallAsync<JsonElement>("vc_checkout", new { repoPath = Device(state).WorktreeRoot, branchName = body.GetProperty("branch").GetString() }, ct));
        return app;

    }

    private static void ApplyDevicePaths(string tool, IDictionary<string, object?> args, DeviceContext device)
    {
        if (tool is "sync_export" or "rebuild_export") args["outputDir"] = device.StagingRoot;
        if (tool is "ingest_source" or "update_components")
        {
            args["exportedSourceRoot"] = device.ExportedSourceRoot;
            args["modifiedSourceRoot"] = device.ModifiedSourceRoot;
            args["dbPath"] = device.KnowledgeDbPath;
        }
        if (tool.StartsWith("vc_", StringComparison.Ordinal)) args["repoPath"] = device.WorktreeRoot;
    }

    private static async Task<T> RunOperationAsync<T>(
        HttpContext http,
        OperationStatusRegistry operations,
        string operationType,
        string initialMessage,
        Func<IOperationProgress?, Task<T>> action,
        string successMessage)
    {
        var operationId = http.Request.Headers["X-Operation-Id"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(operationId))
            operations.Start(operationId, operationType, initialMessage);

        try
        {
            var result = await action(
                string.IsNullOrWhiteSpace(operationId) ? null : operations.For(operationId));
            if (!string.IsNullOrWhiteSpace(operationId))
                operations.Succeed(operationId, successMessage);
            return result;
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(operationId))
            {
                var message = operations.TryGet(operationId, out var snapshot)
                    ? snapshot.Message
                    : initialMessage;
                operations.Fail(operationId, message, exception.Message);
            }
            throw;
        }
    }
}

public sealed record ChatTurnSnapshot(UsageInfo? Usage, bool HitRoundCap, int Compactions);

internal sealed class ApiChatService(
    IServiceProvider services,
    IConfiguration configuration,
    CompatibilityRuntimeState state,
    DeviceToolArgumentBinder binder,
    PendingToolActions pending,
    SandboxPolicy policy)
{
    public const int DefaultContextWindow = 128_000;
    private sealed record ActiveChat(AgentLoop Loop, ChatSessionData Session);
    private readonly ConcurrentDictionary<string, ActiveChat> chats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ChatSessionData> pendingSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> sessionRequired = new(StringComparer.Ordinal);

    public void Reset()
    {
        foreach (var pair in chats)
            pendingSessions[pair.Key] = pair.Value.Session;
        chats.Clear();
        state.IncrementChatGeneration();
    }
    public bool RequiresExplicitSession(DeviceContext device) =>
        sessionRequired.ContainsKey(DeviceContextIdentity.Key(device));

    public string? ActiveSessionId(DeviceContext device) =>
        chats.TryGetValue(DeviceContextIdentity.Key(device), out var active)
            ? active.Session.Header.SessionId
            : pendingSessions.TryGetValue(DeviceContextIdentity.Key(device), out var pending)
                ? pending.Header.SessionId : null;
    public IReadOnlyList<ChatMessage> History(DeviceContext device) =>
        chats.TryGetValue(DeviceContextIdentity.Key(device), out var active)
            ? active.Loop.History : Array.Empty<ChatMessage>();

    /// <summary>Last-turn state for the UI: exact context size (last billed prompt), the round-cap flag, compaction count.</summary>
    public ChatTurnSnapshot TurnSnapshot(DeviceContext device) =>
        chats.TryGetValue(DeviceContextIdentity.Key(device), out var active)
            ? new ChatTurnSnapshot(
                active.Loop.RoundUsages.LastOrDefault(usage => usage is not null),
                active.Loop.LastTurnHitRoundCap,
                active.Loop.LastTurnCompactions)
            : new ChatTurnSnapshot(null, false, 0);

    /// <summary>Extends the active loop's round budget (the "continue" affordance after a round cap).</summary>
    public bool GrantMoreRounds(DeviceContext device, int additional)
    {
        if (!chats.TryGetValue(DeviceContextIdentity.Key(device), out var active)) return false;
        active.Loop.GrantMoreRounds(additional);
        return true;
    }
    public void Clear(DeviceContext device)
    {
        var key = DeviceContextIdentity.Key(device);
        if (!chats.TryGetValue(key, out var active)) return;
        active.Loop.ClearHistory();
        var cleared = active.Session with { Messages = [], RoundUsages = [] };
        SessionManager.SaveSession(device, cleared);
        chats[key] = active with { Session = cleared };
    }
    public void DeleteSession(DeviceContext device, string sessionId)
    {
        var key = DeviceContextIdentity.Key(device);
        if (chats.TryGetValue(key, out var active)
            && active.Session.Header.SessionId == sessionId)
            chats.TryRemove(key, out _);
        if (pendingSessions.TryGetValue(key, out var pending)
            && pending.Header.SessionId == sessionId)
            pendingSessions.TryRemove(key, out _);
        sessionRequired[key] = 0;
        SessionManager.DeleteSession(device, sessionId);
    }

    public ChatSessionData CreateSession(DeviceContext device)
    {
        var key = DeviceContextIdentity.Key(device);
        sessionRequired.TryRemove(key, out _);
        chats.TryRemove(key, out _);
        var session = SessionManager.CreateNewSession(device, new ChatRequestSettings(), null);
        pendingSessions[key] = session;
        return session;
    }

    public ChatSessionData? LoadSession(DeviceContext device, string sessionId)
    {
        var session = SessionManager.LoadSession(device, sessionId);
        if (session is null) return null;
        var key = DeviceContextIdentity.Key(device);
        sessionRequired.TryRemove(key, out _);
        if (chats.TryGetValue(key, out var active))
        {
            active.Loop.RestoreFrom(session.Messages, session.RoundUsages);
            chats[key] = active with { Session = session };
        }
        else pendingSessions[key] = session;
        return session;
    }

    public ChatSessionData? RenameSession(
        DeviceContext device,
        string sessionId,
        string title)
    {
        var session = SessionManager.RenameSession(device, sessionId, title);
        if (session is null)
            return null;

        var key = DeviceContextIdentity.Key(device);
        if (chats.TryGetValue(key, out var active)
            && active.Session.Header.SessionId == sessionId)
        {
            chats[key] = active with { Session = session };
        }
        if (pendingSessions.TryGetValue(key, out var pending)
            && pending.Header.SessionId == sessionId)
        {
            pendingSessions[key] = session;
        }
        return session;
    }

    public Task<string> RunAsync(DeviceContext device, string message, CancellationToken token) =>
        RunStreamingAsync(device, message, _ => { }, (_, _) => { }, token);

    public async Task<string> RunStreamingAsync(
        DeviceContext device,
        string message,
        Action<string> progress,
        Action<string, string> streamDelta,
        CancellationToken token)
    {
        var active = await EnsureActiveChatAsync(device, token);
        void OnProgress(string line) => progress(line);
        void OnDelta(string kind, string delta) => streamDelta(kind, delta);

        active.Loop.Progress += OnProgress;
        active.Loop.StreamDelta += OnDelta;
        try
        {
            var answer = await active.Loop.RunAsync(message, token);
            SaveActiveSession(device, active, message);
            return answer;
        }
        finally
        {
            active.Loop.Progress -= OnProgress;
            active.Loop.StreamDelta -= OnDelta;
        }
    }

    private async Task<ActiveChat> EnsureActiveChatAsync(DeviceContext device, CancellationToken token)
    {
        var apiKey = state.ApiKey ?? configuration["DeepSeek:ApiKey"] ?? configuration["deepSeekApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("CHAT_API_KEY_REQUIRED");
        var runtime = services.GetService<McpRuntime>()
            ?? throw new InvalidOperationException("CHAT_MCP_RUNTIME_UNAVAILABLE");
        var contextKey = DeviceContextIdentity.Key(device);
        if (sessionRequired.ContainsKey(contextKey))
            throw new InvalidOperationException("CHAT_SESSION_REQUIRED");
        if (!chats.TryGetValue(contextKey, out var active))
        {
            var session = pendingSessions.TryRemove(contextKey, out var restored)
                ? restored
                : SessionManager.CreateNewSession(device, Settings(configuration, state), null);
            var discovered = await McpToolCatalog.BuildAsync(runtime.Host, token);
            var catalog = new McpToolCatalog(discovered.Tools.Select(spec => spec with
            {
                Caller = new BoundMcpCaller(spec.Caller, binder, device),
            }));
            var sandbox = new AgentSandbox(policy, 20, request =>
            {
                var completion = new TaskCompletionSource<ToolConfirmation>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var confirmationRequester = session.Header.SessionId;
                var id = pending.Add(contextKey, confirmationRequester, (decision, _) =>
                {
                    completion.TrySetResult(decision);
                    return Task.FromResult<object?>(new { status = decision.ToString() });
                });
                state.Logs.Enqueue(JsonSerializer.Serialize(new
                {
                    kind = "confirmation",
                    id,
                    requester = confirmationRequester,
                    toolName = request.ToolName,
                    arguments = request.ArgumentsSummary,
                }));
                return completion.Task;
            });
            var loop = new AgentLoop(
                new DeepSeekClient(apiKey, configuration["DeepSeek:BaseUrl"] ?? "https://api.deepseek.com"),
                catalog,
                () => string.Join('\n',
                    $"Workbench: {device.WorkbenchId}",
                    $"Worktree: {device.WorktreeId}",
                    $"Device: {device.DeviceId}",
                    $"Exported source: {device.ExportedSourceRoot}",
                    $"Modified source: {device.ModifiedSourceRoot}",
                    $"Knowledge DB: {device.KnowledgeDbPath}"),
                Settings(configuration, state),
                sandbox);
            loop.Apply(LoopPolicy(configuration, state));
            if (restored is not null) loop.RestoreFrom(restored.Messages, restored.RoundUsages);
            active = new ActiveChat(loop, session);
            chats[contextKey] = active;
        }
        return active;
    }

    private void SaveActiveSession(DeviceContext device, ActiveChat active, string message)
    {
        var contextKey = DeviceContextIdentity.Key(device);
        var updated = active.Session with
        {
            Messages = active.Loop.History.ToList(),
            RoundUsages = active.Loop.RoundUsages.ToList(),
            Header = active.Session.Header with
            {
                UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                Title = SessionManager.IsDefaultTitle(active.Session.Header.Title)
                    ? SessionManager.DeriveTitle(message)
                    : active.Session.Header.Title,
            },
        };
        SessionManager.SaveSession(device, updated);
        chats[contextKey] = active with { Session = updated };
    }

    /// <summary>Model context window used for the "context: X / Y" display; overridable via chatSettings:contextWindow.</summary>
    internal static int ContextWindow(IConfiguration configuration) =>
        int.TryParse(configuration["chatSettings:contextWindow"] ?? configuration["deepSeekContextWindow"], out var window) && window > 0
            ? window
            : DefaultContextWindow;

    /// <summary>Tunable AgentLoop limits: live chat-settings JSON first, then chatSettings:* config.</summary>
    internal static ChatLoopPolicy LoopPolicy(
        IConfiguration configuration,
        CompatibilityRuntimeState state)
    {
        var live = state.ChatSettings;
        string? Live(string name) => live is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty(name, out var property) ? property.ToString() : null;
        int Int(string name, int fallback) =>
            int.TryParse(Live(name) ?? configuration[$"chatSettings:{name}"], out var value) && value > 0 ? value : fallback;
        var defaults = new ChatLoopPolicy();
        return new ChatLoopPolicy
        {
            RoundLimit = Int("roundLimit", defaults.RoundLimit),
            PromptTokenBudget = Int("promptTokenBudget", defaults.PromptTokenBudget),
            PromptTokenWarningThreshold = Int("promptTokenWarningThreshold", defaults.PromptTokenWarningThreshold),
            ToolResultMaxChars = Int("toolResultMaxChars", defaults.ToolResultMaxChars),
            ToolResultCompactChars = Int("toolResultCompactChars", defaults.ToolResultCompactChars),
            HistoryTokenThreshold = Int("historyTokenThreshold", defaults.HistoryTokenThreshold),
            RecentTurnsToKeep = Int("recentTurnsToKeep", defaults.RecentTurnsToKeep),
            CollapsedAnswerChars = Int("collapsedAnswerChars", defaults.CollapsedAnswerChars),
        };
    }

    internal static ChatRequestSettings Settings(
        IConfiguration configuration,
        CompatibilityRuntimeState state)
    {
        var live = state.ChatSettings;
        string? Live(string name) => live is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty(name, out var property) ? property.ToString() : null;
        return new ChatRequestSettings
        {
            Model = Live("model") ?? configuration["chatSettings:model"] ?? configuration["deepSeekModel"] ?? "deepseek-v4-flash",
            ThinkingEnabled = bool.TryParse(Live("thinkingEnabled") ?? configuration["chatSettings:thinkingEnabled"] ?? configuration["deepSeekThinkingEnabled"], out var thinking) ? thinking : ChatRequestSettings.DefaultThinkingEnabled,
            ReasoningEffort = Live("reasoningEffort") ?? configuration["chatSettings:reasoningEffort"] ?? configuration["deepSeekReasoningEffort"] ?? "high",
            Temperature = double.TryParse(Live("temperature") ?? configuration["chatSettings:temperature"] ?? configuration["deepSeekTemperature"], out var temperature) ? temperature : 1.0,
            TopP = double.TryParse(Live("topP") ?? configuration["chatSettings:topP"] ?? configuration["deepSeekTopP"], out var topP) ? topP : 1.0,
        };
    }

    private sealed class BoundMcpCaller(
        IMcpToolCaller inner,
        DeviceToolArgumentBinder binder,
        DeviceContext device) : IMcpToolCaller
    {
        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            var supplied = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(args)) ?? new();
            return inner.CallAsync<T>(tool, binder.Bind(tool, supplied, device), cancellationToken);
        }
    }
}
