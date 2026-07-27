using System.Collections.Concurrent;
using System.Text.Json;
using Agent.Mcp;
using Agent.Chat;
using Agent.Workbench;

public sealed record CompatibilityToolCallRequest(string Tool, Dictionary<string, object?>? Arguments);
public sealed record CompatibilityPathRequest(string? FilePath, string? Message, string[]? Paths);

public sealed class ApiMcpGateway(
    IMcpToolCaller engineering,
    IMcpToolCaller knowledge,
    IMcpToolCaller versionControl,
    IMcpToolCaller sourceEditor)
{
    public IMcpToolCaller For(string tool) =>
        tool.StartsWith("vc_", StringComparison.Ordinal) ? versionControl :
        tool.StartsWith("query_", StringComparison.Ordinal) || tool is "ingest_source" or "update_components" ? knowledge :
        tool.StartsWith("edit_", StringComparison.Ordinal) || tool.StartsWith("compose_", StringComparison.Ordinal) ? sourceEditor :
        engineering;
}

public sealed class CompatibilityRuntimeState
{
    public ConcurrentQueue<string> Logs { get; } = new();
    public ConcurrentQueue<JsonElement> ChatHistory { get; } = new();
    public string? ApiKey { get; set; }
}

public static class CompatibilityEndpoints
{
    public static IEndpointRouteBuilder MapCompatibilityEndpoints(this IEndpointRouteBuilder app)
    {
        static DeviceContext Device(WorkbenchApiState state) =>
            state.Selection?.DeviceId is { } id
                ? state.Device(id).Context
                : throw new InvalidOperationException("DEVICE_SELECTION_REQUIRED");

        app.MapPost("/api/connect", async (JsonElement body, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("connect").CallAsync<object>("connect", body, ct));
        app.MapPost("/api/disconnect", async (ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("disconnect").CallAsync<object>("disconnect", new { }, ct));
        app.MapGet("/api/connections", () => Results.Ok(Array.Empty<object>()));
        app.MapPost("/api/connections/switch", async (JsonElement body, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("connect").CallAsync<object>("connect", body, ct));
        app.MapGet("/api/tools", () => Results.Ok(Array.Empty<object>()));
        app.MapGet("/api/sessions", (WorkbenchApiState state) => SessionManager.ListSessions(Device(state)));

        app.MapPost("/api/tools/call", async (CompatibilityToolCallRequest request, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
        {
            var device = Device(state);
            var args = request.Arguments ?? new Dictionary<string, object?>();
            ApplyDevicePaths(request.Tool, args, device);
            return await gateway.For(request.Tool).CallAsync<JsonElement>(request.Tool, args, ct);
        });
        app.MapPost("/api/chat/confirm/{id}", (string id, JsonElement body) => Results.Ok(new { id, accepted = true }));
        app.MapGet("/api/logs", (CompatibilityRuntimeState state) => state.Logs.ToArray());
        app.MapGet("/api/chat/history", (CompatibilityRuntimeState state) => state.ChatHistory.ToArray());
        app.MapPost("/api/chat/clear", (CompatibilityRuntimeState state) =>
        {
            state.ChatHistory.Clear();
            return Results.NoContent();
        });
        app.MapPost("/api/chat", async (JsonElement body, WorkbenchApiState state, ApiChatService chat, CancellationToken ct) =>
        {
            var device = Device(state);
            var message = body.TryGetProperty("message", out var value) ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("message is required.");
            return Results.Ok(new { content = await chat.RunAsync(device, message, ct) });
        });
        app.MapGet("/api/config/key/status", (CompatibilityRuntimeState state) => Results.Ok(new { configured = !string.IsNullOrWhiteSpace(state.ApiKey) }));
        app.MapPost("/api/config/key", (JsonElement body, CompatibilityRuntimeState state) =>
        {
            state.ApiKey = body.GetProperty("apiKey").GetString();
            return Results.NoContent();
        });
        app.MapPost("/api/config/settings", (JsonElement _) => Results.NoContent());
        app.MapGet("/api/chat/sessions", (WorkbenchApiState state) => SessionManager.ListSessions(Device(state)));
        app.MapPost("/api/chat/session/load", (JsonElement body, WorkbenchApiState state) =>
        {
            var id = body.GetProperty("sessionId").GetString() ?? throw new ArgumentException("sessionId is required.");
            return SessionManager.LoadSession(Device(state), id) is { } session ? Results.Ok(session) : Results.NotFound();
        });
        app.MapPost("/api/chat/session/delete", (JsonElement body, WorkbenchApiState state) =>
        {
            var id = body.GetProperty("sessionId").GetString() ?? throw new ArgumentException("sessionId is required.");
            SessionManager.DeleteSession(Device(state), id);
            return Results.NoContent();
        });
        app.MapGet("/api/chat/session/info", (WorkbenchApiState state) => Results.Ok(new
        {
            selection = state.Selection,
            sessions = SessionManager.ListSessions(Device(state)).Count,
        }));

        app.MapGet("/api/project/info", async (WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
        {
            _ = Device(state);
            return await gateway.For("get_project_info").CallAsync<JsonElement>("get_project_info", new { }, ct);
        });
        app.MapGet("/api/blocks", async (WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
        {
            var selected = state.Device(Device(state).DeviceId);
            return await gateway.For("list_blocks").CallAsync<JsonElement>("list_blocks", new { plcName = selected.Metadata.PlcName }, ct);
        });
        app.MapPost("/api/project/select-plc", (WorkbenchApiState state) => Results.Ok(new { deviceId = Device(state).DeviceId }));
        app.MapGet("/api/project/context-status", (WorkbenchApiState state) =>
        {
            var device = Device(state);
            return Results.Ok(new { device.DeviceId, device.ExportedSourceRoot, device.ModifiedSourceRoot, device.StagingRoot, device.KnowledgeDbPath });
        });
        app.MapGet("/api/project/compare", (WorkbenchApiState state, WorkbenchCoordinator coordinator) =>
            coordinator.PreviewRefresh(Device(state)));
        app.MapGet("/api/check-environment", (WorkbenchApiState state) => Results.Ok(new { selected = Device(state).DeviceId }));
        app.MapGet("/api/browse", (string? path, WorkbenchApiState state) =>
        {
            var device = Device(state);
            var resolved = string.IsNullOrWhiteSpace(path) ? device.WorktreeRoot : WorkbenchPaths.ResolveRelative(device.WorktreeRoot, path);
            return Results.Ok(new { path = resolved, entries = Directory.Exists(resolved) ? Directory.EnumerateFileSystemEntries(resolved).Select(Path.GetFileName) : [] });
        });
        app.MapGet("/api/knowledge/node-kinds", async (WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("query_node_kinds").CallAsync<JsonElement>("query_node_kinds", new { dbPath = Device(state).KnowledgeDbPath }, ct));
        app.MapGet("/api/knowledge/nodes", async (string? kind, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("query_nodes").CallAsync<JsonElement>("query_nodes", new { dbPath = Device(state).KnowledgeDbPath, kind }, ct));
        app.MapGet("/api/knowledge/edge-types", async (WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("query_edge_types").CallAsync<JsonElement>("query_edge_types", new { dbPath = Device(state).KnowledgeDbPath }, ct));
        app.MapGet("/api/knowledge/edges", async (string fromNodeId, string? type, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("query_edges").CallAsync<JsonElement>("query_edges", new { dbPath = Device(state).KnowledgeDbPath, fromNodeId, type }, ct));
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
        app.MapPost("/api/vc/restore", async (CompatibilityPathRequest body, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_restore").CallAsync<JsonElement>("vc_restore", new { repoPath = Device(state).WorktreeRoot, filePath = body.FilePath }, ct));
        app.MapGet("/api/vc/branches", async (WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_branches").CallAsync<JsonElement>("vc_branches", new { repoPath = Device(state).WorktreeRoot }, ct));
        app.MapPost("/api/vc/checkout", async (JsonElement body, WorkbenchApiState state, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_checkout").CallAsync<JsonElement>("vc_checkout", new { repoPath = Device(state).WorktreeRoot, branch = body.GetProperty("branch").GetString() }, ct));
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
}

internal sealed class ApiChatService(
    IServiceProvider services,
    IConfiguration configuration,
    CompatibilityRuntimeState state)
{
    private readonly ConcurrentDictionary<string, AgentLoop> loops = new(StringComparer.Ordinal);

    public async Task<string> RunAsync(DeviceContext device, string message, CancellationToken token)
    {
        var apiKey = state.ApiKey ?? configuration["DeepSeek:ApiKey"] ?? configuration["deepSeekApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("CHAT_API_KEY_REQUIRED");
        var runtime = services.GetService<McpRuntime>()
            ?? throw new InvalidOperationException("CHAT_MCP_RUNTIME_UNAVAILABLE");
        if (!loops.TryGetValue(device.DeviceId, out var loop))
        {
            var catalog = await McpToolCatalog.BuildAsync(runtime.Host, token);
            loop = new AgentLoop(
                new DeepSeekClient(apiKey, configuration["DeepSeek:BaseUrl"] ?? "https://api.deepseek.com"),
                catalog,
                () => string.Join('\n',
                    $"Workbench: {device.WorkbenchId}",
                    $"Worktree: {device.WorktreeId}",
                    $"Device: {device.DeviceId}",
                    $"Exported source: {device.ExportedSourceRoot}",
                    $"Modified source: {device.ModifiedSourceRoot}",
                    $"Knowledge DB: {device.KnowledgeDbPath}"));
            loops[device.DeviceId] = loop;
        }
        return await loop.RunAsync(message, token);
    }
}
