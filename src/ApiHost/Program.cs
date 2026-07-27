using Agent;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Contracts.Engineering;
using EngConnectionInfo = Contracts.Engineering.ConnectionInfo;
using Contracts.Sandbox;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

/* ── Config ─────────────────────────────────────────── */

const string SolutionFileName = "AgentAssistPlcDev.sln";
const string ConfigDirName = "PlcAiAssistant";
const string ConfigFileName = "config.json";

#if DEBUG
const string BuildConfiguration = "Debug";
#else
const string BuildConfiguration = "Release";
#endif

static string FindSolutionDirectory(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException($"Could not find {SolutionFileName} above '{start}'.");
}

static AppConfig LoadConfig()
{
    var configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ConfigDirName);
    var configPath = Path.Combine(configDir, ConfigFileName);

    string? engineeringOverride = null, knowledgeOverride = null, versionControlOverride = null, sourceEditorOverride = null;
    string? apiKey = null, model = null, baseUrl = null, effort = null;
    bool? thinking = null;
    double? temperature = null, topP = null;

    if (File.Exists(configPath))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;

            static string? GetStr(JsonElement e, string p) =>
                e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            static bool? GetBool(JsonElement e, string p) =>
                e.TryGetProperty(p, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            static double? GetDbl(JsonElement e, string p) =>
                e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

            engineeringOverride = GetStr(root, "engineeringServerPath");
            knowledgeOverride = GetStr(root, "knowledgeServerPath");
            versionControlOverride = GetStr(root, "versionControlServerPath");
            sourceEditorOverride = GetStr(root, "sourceEditorServerPath");
            apiKey = GetStr(root, "deepSeekApiKey");
            model = GetStr(root, "deepSeekModel");
            baseUrl = GetStr(root, "deepSeekBaseUrl");
            thinking = GetBool(root, "deepSeekThinkingEnabled");
            effort = GetStr(root, "deepSeekReasoningEffort");
            temperature = GetDbl(root, "deepSeekTemperature");
            topP = GetDbl(root, "deepSeekTopP");
        }
        catch (JsonException) { /* use defaults */ }
    }

    string engPath, knowPath, vcPath, sourceEditorPath;
    if (engineeringOverride != null && knowledgeOverride != null)
    {
        engPath = engineeringOverride;
        knowPath = knowledgeOverride;
        vcPath = versionControlOverride ?? ResolveVcPath();
        sourceEditorPath = sourceEditorOverride ?? ResolveSourceEditorPath();
    }
    else
    {
        var slnDir = FindSolutionDirectory(AppContext.BaseDirectory);
        engPath = engineeringOverride
            ?? Path.Combine(slnDir, "src", "Mcp.Engineering", "bin", BuildConfiguration, "net48", "Mcp.Engineering.exe");
        knowPath = knowledgeOverride
            ?? Path.Combine(slnDir, "src", "Mcp.Knowledge", "bin", BuildConfiguration, "net8.0", "Mcp.Knowledge.exe");
        vcPath = versionControlOverride ?? ResolveVcPath();
        sourceEditorPath = sourceEditorOverride ?? ResolveSourceEditorPath();
    }

    return new AppConfig(engPath, knowPath, vcPath, sourceEditorPath)
    {
        DeepSeekApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
        DeepSeekModel = model ?? "deepseek-v4-flash",
        DeepSeekBaseUrl = baseUrl ?? "https://api.deepseek.com",
        DeepSeekThinkingEnabled = thinking ?? true,
        DeepSeekReasoningEffort = effort ?? "high",
        DeepSeekTemperature = temperature ?? 1.0,
        DeepSeekTopP = topP ?? 1.0,
    };
}

static string ResolveVcPath()
{
    var slnDir = FindSolutionDirectory(AppContext.BaseDirectory);
    return Path.Combine(slnDir, "src", "Mcp.VersionControl", "bin", BuildConfiguration, "net8.0", "Mcp.VersionControl.exe");
}

static string ResolveSourceEditorPath()
{
    var slnDir = FindSolutionDirectory(AppContext.BaseDirectory);
    return Path.Combine(slnDir, "src", "Mcp.SourceEditor", "bin", BuildConfiguration, "net8.0", "Mcp.SourceEditor.exe");
}

static void SaveApiKey(string apiKey)
{
    var configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ConfigDirName);
    var configPath = Path.Combine(configDir, ConfigFileName);

    JsonObject root;
    try
    {
        root = File.Exists(configPath)
            ? JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject()
            : new JsonObject();
    }
    catch { root = new JsonObject(); }

    root["deepSeekApiKey"] = apiKey.Trim();
    Directory.CreateDirectory(configDir);
    File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

static void SaveChatSettings(string model, bool thinkingEnabled, string reasoningEffort, double temperature, double topP)
{
    var configDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ConfigDirName);
    var configPath = Path.Combine(configDir, ConfigFileName);

    JsonObject root;
    try
    {
        root = File.Exists(configPath)
            ? JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject()
            : new JsonObject();
    }
    catch { root = new JsonObject(); }

    root["deepSeekModel"] = model;
    root["deepSeekThinkingEnabled"] = thinkingEnabled;
    root["deepSeekReasoningEffort"] = reasoningEffort;
    root["deepSeekTemperature"] = temperature;
    root["deepSeekTopP"] = topP;
    Directory.CreateDirectory(configDir);
    File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

/* ── Bootstrap ──────────────────────────────────────── */

var config = LoadConfig();
Console.Error.WriteLine($"Engineering server: {config.EngineeringServerPath}");
Console.Error.WriteLine($"Knowledge server: {config.KnowledgeServerPath}");
Console.Error.WriteLine($"Version control server: {config.VersionControlServerPath}");
Console.Error.WriteLine($"Source editor server: {config.SourceEditorServerPath}");
Console.Error.WriteLine($"DeepSeek: {config.DeepSeekBaseUrl} model={config.DeepSeekModel}");

/* ── Log buffer (ring buffer + subscriber channels) ──── */
var logBuffer = new LogBuffer();
void Log(string line)
{
    Console.Error.WriteLine(line);
    logBuffer.Write(line);
}

/// <summary>Record the source TIA project path into the project-level metadata.json so the
/// frontend Projects panel can offer direct "Open with UI/background" without repicking.</summary>
void RecordSourcePath(string projectName, string sourcePath)
{
    var exportRoot = AssistantPaths.ResolveExportRoot(projectName);
    try
    {
        var doc = new JsonObject();
        if (File.Exists(Path.Combine(exportRoot, "metadata.json")))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(Path.Combine(exportRoot, "metadata.json")));
                if (node is JsonObject existing) doc = existing;
            }
            catch { }
        }
        var current = doc["sourceProjectPath"]?.GetValue<string>();
        if (current != sourcePath)
        {
            doc["sourceProjectPath"] = sourcePath;
            Directory.CreateDirectory(exportRoot);
            File.WriteAllText(Path.Combine(exportRoot, "metadata.json"),
                doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    catch (Exception ex) { Log($"failed to record source path in project metadata: {ex.Message}"); }
}

var host = new McpHost(config.EngineeringServerPath, config.KnowledgeServerPath,
    config.VersionControlServerPath, config.SourceEditorServerPath);
host.ServerLog += line => Log(line);
await host.StartAsync();
Log("MCP servers running.");

/* ── Git init for existing export roots ────────────── */
// Legacy exports are intentionally neither migrated nor mutated.

var catalog = await McpToolCatalog.BuildAsync(host);
Log($"Agent: {catalog.Tools.Count} MCP tools exposed.");

/* ── Agent state ────────────────────────────────────── */
// Recreated when the API key changes; holds the conversation loop + sandbox.
AgentLoop? agentLoop = null;
DeepSeekClient? deepSeekClient = null;

/* ── Session management (persistent per-project chat sessions) ── */
// Session files live at {exportRoot}\sessions\{sessionId}.json.
ChatSessionData? currentSession = null;

/* ── Connection registry (multi-project) ─────────────── */
// Tracks connections. The Engineering MCP server supports one TIA project at a time,
// so one connection is "active". The registry lets the UI show workspace projects.
var _connections = new ConcurrentDictionary<string, ConnectionEntry>(StringComparer.Ordinal);
string? _activeConnectionId = null;

/// <summary>Name of the workbench project selected for offline viewing (no TIA connection required).</summary>
string? _selectedProjectName = null;

Func<ConnectionEntry?> ActiveConnection = () =>
    _activeConnectionId != null && _connections.TryGetValue(_activeConnectionId, out var c) ? c : null;

string? ResolveRepoPath()
{
    var active = ActiveConnection();
    if (active != null) return AssistantPaths.ResolveExportRoot(active.ProjectName);
    if (_selectedProjectName != null) return AssistantPaths.ResolveExportRoot(_selectedProjectName);
    return null;
}

void CreateLoop(AppConfig cfg)
{
    if (!cfg.HasDeepSeekApiKey) { agentLoop = null; deepSeekClient = null; return; }

    var sandboxConfig = SandboxConfig.LoadDefault();
    var sandbox = new AgentSandbox(
        sandboxConfig.Policy,
        sandboxConfig.MaxDestructiveCallsPerSession,
        confirm: async request =>
        {
            var handler = ConfirmChannel.Handler;
            if (handler == null)
            {
                return ToolConfirmation.Deny;
            }
            return await handler(request);
        },
        new SandboxAudit(sandboxConfig.AuditDirectory, "apihost"));

    deepSeekClient = new DeepSeekClient(cfg.DeepSeekApiKey!, cfg.DeepSeekBaseUrl);
    agentLoop = new AgentLoop(deepSeekClient, catalog,
        () => BuildRuntimeContext(currentSession?.Header.ProjectName),
        new ChatRequestSettings
        {
            Model = cfg.DeepSeekModel,
            ThinkingEnabled = cfg.DeepSeekThinkingEnabled,
            ReasoningEffort = cfg.DeepSeekReasoningEffort,
            Temperature = cfg.DeepSeekTemperature,
            TopP = cfg.DeepSeekTopP,
        }, sandbox);
}

/// <summary>
/// Build the runtime context block for the system prompt.
/// When <paramref name="projectName"/> is provided (session-bound), the context references
/// that project regardless of the live TIA connection state — chat is decoupled from TIA.
/// When omitted, falls back to the active connection or selected offline project.
/// </summary>
string BuildRuntimeContext(string? projectName = null)
{
    var active = ActiveConnection();
    var proj = projectName ?? active?.ProjectName ?? _selectedProjectName;
    var lines = new List<string>
    {
        $"TIA connection: {(active != null ? "Connected" : "Not connected")}",
    };

    if (proj != null)
    {
        lines.Add($"Project: {proj}");
        var exportRoot = AssistantPaths.ResolveExportRoot(proj);
        lines.Add($"Export root: {exportRoot}");
        var dbPath = AssistantPaths.ResolveKnowledgeDbPath(proj);
        lines.Add(File.Exists(dbPath)
            ? $"Knowledge DB: {dbPath}"
            : "Knowledge DB: (not yet built — use 'Read Project Context' in the workspace)");
    }
    else if (active != null)
    {
        lines.Add($"Project: {active.ProjectName}");
        lines.Add($"Export root: {AssistantPaths.ResolveExportRoot(active.ProjectName)}");
    }

    return string.Join('\n', lines);
}

if (config.HasDeepSeekApiKey) CreateLoop(config);

/* ── Web Application ────────────────────────────────── */

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors();
var metadataStore = new AtomicJsonStore();
var workbenchCatalog = new WorkbenchCatalog(metadataStore);
var sourceResolver = new DeviceSourceResolver(device =>
{
    var path = Path.Combine(device.DeviceRoot, "device.json");
    var metadata = metadataStore.Read<DeviceMetadata>(path);
    metadataStore.Write(path, metadata with
    {
        Knowledge = metadata.Knowledge with { Stale = true },
    });
});
var coordinator = new WorkbenchCoordinator(
    host.Engineering, host.Knowledge, host.VersionControl!,
    workbenchCatalog, metadataStore, new DeviceReconciler(), sourceResolver);
builder.Services.AddSingleton(metadataStore);
builder.Services.AddSingleton(workbenchCatalog);
builder.Services.AddSingleton(sourceResolver);
builder.Services.AddSingleton(coordinator);
var workbenchApiState = new WorkbenchApiState(workbenchCatalog, metadataStore);
builder.Services.AddSingleton(workbenchApiState);

var app = builder.Build();
app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
app.MapWorkbenchEndpoints();
DeviceContext? SelectedDevice() =>
    workbenchApiState.Selection?.DeviceId is { } id ? workbenchApiState.Device(id).Context : null;

/* ── API Endpoints ──────────────────────────────────── */

// Status
app.MapGet("/api/status", () => Results.Ok(new
{
    servers = host.Engineering.IsRunning
        && host.Knowledge.IsRunning
        && (host.VersionControl?.IsRunning ?? true)
        && (host.SourceEditor?.IsRunning ?? true)
            ? "running"
            : "starting",
    connected = ActiveConnection()?.ProjectName,
    selectedProject = _selectedProjectName,
    connections = _connections.Count,
    chatReady = agentLoop != null,
    tools = catalog.Tools.Count,
}));

// List MCP tools
app.MapGet("/api/tools", () =>
    Results.Ok(catalog.Tools.Select(t => new { t.Name, t.Description, t.ServerName, Schema = t.InputSchema })));

// List TIA sessions
app.MapGet("/api/sessions", async () =>
{
    try
    {
        var sessions = await host.Engineering.CallAsync<SessionInfo[]>("list_sessions", new { });
        return Results.Ok(sessions);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

// Connect to TIA
app.MapPost("/api/connect", async (ConnectRequest req) =>
{
    try
    {
        var args = req.SessionId.HasValue
            ? new { sessionId = req.SessionId }
            : (object)new { projectPath = req.ProjectPath, withUI = req.WithUI ?? false, timeoutSeconds = req.TimeoutSeconds ?? 60 };
        var info = await host.Engineering.CallAsync<EngConnectionInfo>("connect", args);

        var id = ConnectionEntry.NewId();
        var entry = new ConnectionEntry(
            Id: id,
            SessionId: req.SessionId?.ToString(),
            ProjectName: info.ProjectName ?? "unknown",
            ProjectPath: info.ProjectPath,
            Attached: info.Attached,
            SelectedPlc: null);
        _connections[id] = entry;
        _activeConnectionId = id;
        Log($"connect OK: {info.ProjectName} ({(info.Attached ? "attached" : "headless")})");

        if (!string.IsNullOrEmpty(info.ProjectPath) && !string.IsNullOrEmpty(info.ProjectName))
            RecordSourcePath(info.ProjectName, info.ProjectPath);

        return Results.Ok(entry);
    }
    catch (ToolCallException ex)
    {
        Log($"connect ERROR: [{ex.Code}] {ex.Message}");
        return Results.Problem($"[{ex.Code}] {ex.Message}", statusCode: 502);
    }
    catch (Exception ex)
    {
        Log($"connect ERROR: {ex.Message}");
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

// Disconnect
app.MapPost("/api/disconnect", async () =>
{
    var active = ActiveConnection();
    if (active == null) return Results.Ok(new { status = "not_connected" });

    try
    {
        await host.Engineering.CallAsync<object>("disconnect", new { });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
    finally
    {
        _connections.TryRemove(active.Id, out _);
        _activeConnectionId = null;
    }
    return Results.Ok(new { status = "disconnected" });
});

// List active connections
app.MapGet("/api/connections", () =>
    Results.Ok(_connections.Values.OrderBy(c => c.ProjectName)));

// Switch active connection
app.MapPost("/api/connections/switch", async (ConnectRequest req) =>
{
    if (ActiveConnection() != null)
    {
        try { await host.Engineering.CallAsync<object>("disconnect", new { }); }
        catch { /* best effort */ }
        var ac = ActiveConnection();
        if (ac != null) _connections.TryRemove(ac.Id, out _);
    }

    try
    {
        var args = req.SessionId.HasValue
            ? new { sessionId = req.SessionId }
            : (object)new { projectPath = req.ProjectPath, withUI = req.WithUI ?? false, timeoutSeconds = req.TimeoutSeconds ?? 60 };
        var info = await host.Engineering.CallAsync<EngConnectionInfo>("connect", args);

        var id = ConnectionEntry.NewId();
        var entry = new ConnectionEntry(
            Id: id,
            SessionId: req.SessionId?.ToString(),
            ProjectName: info.ProjectName ?? "unknown",
            ProjectPath: info.ProjectPath,
            Attached: info.Attached,
            SelectedPlc: null);
        _connections[id] = entry;
        _activeConnectionId = id;

        if (!string.IsNullOrEmpty(info.ProjectPath) && !string.IsNullOrEmpty(info.ProjectName))
            RecordSourcePath(info.ProjectName, info.ProjectPath);

        return Results.Ok(entry);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

// Select PLC device
app.MapPost("/api/project/select-plc", (SelectPlcRequest req) =>
{
    if (_activeConnectionId == null || !_connections.TryGetValue(_activeConnectionId, out var entry))
        return Results.BadRequest(new { error = "No active connection." });

    var updated = entry with { SelectedPlc = req.PlcName };
    _connections[_activeConnectionId] = updated;
    return Results.Ok(updated);
});

// Context status
app.MapGet("/api/project/context-status", async (string? outputDir, string? plcName) =>
{
    var active = ActiveConnection();
    if (active == null) return Results.BadRequest(new { error = "No active connection." });

    var exportRoot = outputDir ?? AssistantPaths.ResolveExportRoot(active.ProjectName);
    // Pass plcName through so the MCP tool can scope per-device internally —
    // the project root stays as-is for reading project-level metadata.
    try
    {
        var args = new Dictionary<string, object?> { ["outputDir"] = exportRoot };
        if (!string.IsNullOrWhiteSpace(plcName)) args["plcName"] = plcName;
        var result = await host.Engineering.CallAsync<ContextStatusResult[]>(
            "get_context_status", args);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

// Compare context
app.MapGet("/api/project/compare", async (string? outputDir, string? plcName) =>
{
    var active = ActiveConnection();
    if (active == null) return Results.BadRequest(new { error = "No active connection." });

    var exportRoot = outputDir ?? AssistantPaths.ResolveExportRoot(active.ProjectName);
    // Pass plcName through so the MCP tool can scope per-device internally.
    try
    {
        var args = new Dictionary<string, object?> { ["outputDir"] = exportRoot };
        if (!string.IsNullOrWhiteSpace(plcName)) args["plcName"] = plcName;
        var result = await host.Engineering.CallAsync<ContextCompareResult[]>(
            "compare_context", args);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

// Environment check
app.MapGet("/api/check-environment", async () =>
{
    try
    {
        var env = await host.Engineering.CallAsync<EnvCheckResult>("check_environment", new { });
        return Results.Ok(env);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

// Browse filesystem
app.MapGet("/api/browse", (string? path) =>
{
    try
    {
        var dir = string.IsNullOrEmpty(path) ? Environment.CurrentDirectory : path;
        var di = new DirectoryInfo(dir);
        if (!di.Exists) return Results.NotFound(new { error = "Directory not found" });

        var directories = di.EnumerateDirectories().Select(d => d.Name).Order().Take(200).ToArray();
        var files = di.EnumerateFiles("*.ap17").Select(f => f.Name).Order().Take(100).ToArray();

        return Results.Ok(new
        {
            path = di.FullName,
            parent = di.Parent?.FullName,
            directories,
            files,
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

// Generic MCP tool call
app.MapPost("/api/tools/call", async (ToolCallRequest req) =>
{
    var connection = req.Server switch
    {
        "knowledge" => host.Knowledge,
        "versioncontrol" => host.VersionControl,
        "sourceeditor" => host.SourceEditor,
        _ => host.Engineering,
    };

    if (connection == null)
        return Results.BadRequest(new { error = $"Server '{req.Server}' is not available.", code = "SERVER_UNKNOWN" });

    if (!SandboxPolicy.Defaults.TryGetValue(req.Tool, out var tier))
        return Results.BadRequest(new { error = $"Tool '{req.Tool}' is not classified in the sandbox policy.", code = "SANDBOX_TOOL_UNKNOWN" });
    if (tier == SandboxTier.Denied)
        return Results.BadRequest(new { error = $"Tool '{req.Tool}' is disabled.", code = "SANDBOX_TOOL_DENIED" });

    var active = ActiveConnection();
    using var emptyDoc = JsonDocument.Parse("{}");
    var args = req.Args ?? emptyDoc.RootElement;

    // Auto-fill outputDir for engineering tools
    if (active != null && req.Tool is "export_block" or "export_all_blocks" or "export_tag_tables"
        or "export_udts" or "sync_export" or "rebuild_export" or "get_context_status" or "compare_context")
    {
        var dict = args.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(args.GetRawText()) ?? new()
            : new Dictionary<string, object?>();
        dict.TryAdd("outputDir", AssistantPaths.ResolveExportRoot(active.ProjectName));
        args = JsonSerializer.SerializeToElement(dict);
    }

    // Auto-fill exportRoot for knowledge ingest_source
    if (active != null && req.Server == "knowledge" && req.Tool == "ingest_source")
    {
        var dict = args.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(args.GetRawText()) ?? new()
            : new Dictionary<string, object?>();
        if (!dict.ContainsKey("exportRoot"))
            dict["exportRoot"] = AssistantPaths.ResolveExportRoot(active.ProjectName);
        if (!dict.ContainsKey("dbPath"))
            dict["dbPath"] = AssistantPaths.ResolveKnowledgeDbPath(active.ProjectName);
        args = JsonSerializer.SerializeToElement(dict);
    }

    // Auto-fill repoPath for version control tools
    if (active != null && req.Server == "versioncontrol")
    {
        var dict = args.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(args.GetRawText()) ?? new()
            : new Dictionary<string, object?>();
        dict.TryAdd("repoPath", AssistantPaths.ResolveExportRoot(active.ProjectName));
        args = JsonSerializer.SerializeToElement(dict);
    }

    // Destructive tools need prior confirmation
    if (tier == SandboxTier.Destructive)
    {
        if (string.IsNullOrEmpty(req.ConfirmId))
        {
            var confirmId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<ToolConfirmation>();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            timeoutCts.Token.Register(() => tcs.TrySetResult(ToolConfirmation.Deny));
            ConfirmChannel.Pending[confirmId] = tcs;

            return Results.Ok(new
            {
                _requiresConfirmation = true,
                _confirmationId = confirmId,
                _tier = "destructive",
                _toolName = req.Tool,
                _summary = args.GetRawText(),
            });
        }

        if (!ConfirmChannel.Pending.TryRemove(req.ConfirmId, out var ptcs) || !ptcs.Task.IsCompleted)
            return Results.BadRequest(new { error = "Confirmation expired.", code = "CONFIRM_EXPIRED" });
    }

    try
    {
        var result = await connection.CallAsync<JsonElement>(req.Tool, args);
        return Results.Ok(new { result });
    }
    catch (ToolCallException ex)
    {
        return Results.Problem($"[{ex.Code}] {ex.Message}", statusCode: 502);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

// Log stream (SSE)
app.MapGet("/api/logs", async (HttpContext ctx) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    var snapshot = logBuffer.Snapshot();
    foreach (var line in snapshot)
    {
        var ev = JsonSerializer.Serialize(new { kind = "log", line });
        await ctx.Response.WriteAsync($"data: {ev}\n\n");
    }

    var subscriber = logBuffer.Subscribe();
    try
    {
        var reader = subscriber.Reader;
        while (await reader.WaitToReadAsync(ctx.RequestAborted))
        {
            while (reader.TryRead(out var line))
            {
                var ev = JsonSerializer.Serialize(new { kind = "log", line });
                await ctx.Response.WriteAsync($"data: {ev}\n\n");
            }
            await ctx.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        logBuffer.Unsubscribe(subscriber);
    }
});

// Chat (SSE stream)
app.MapPost("/api/chat", async (HttpContext ctx, ChatRequest req) =>
{
    if (agentLoop == null)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("{\"error\":\"DeepSeek API key not configured. POST /api/config/key first.\"}");
        return;
    }

    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    void OnProgress(string line) =>
        ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { kind = "progress", delta = line })}\n\n").Wait();

    void OnStreamDelta(string kind, string delta) =>
        ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { kind, delta })}\n\n").Wait();

    agentLoop.Progress += OnProgress;
    agentLoop.StreamDelta += OnStreamDelta;

    ConfirmChannel.Handler = async request =>
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ToolConfirmation>();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        timeoutCts.Token.Register(() => tcs.TrySetResult(ToolConfirmation.Deny));
        ctx.RequestAborted.Register(() => tcs.TrySetResult(ToolConfirmation.Deny));

        ConfirmChannel.Pending[id] = tcs;

        try
        {
            var ev = JsonSerializer.Serialize(new
            {
                kind = "confirmation",
                id,
                toolName = request.ToolName,
                arguments = request.ArgumentsSummary,
                destructiveCallsSoFar = request.DestructiveCallsSoFar,
                budget = request.SessionBudget,
            });
            await ctx.Response.WriteAsync($"data: {ev}\n\n");
            await ctx.Response.Body.FlushAsync();

            return await tcs.Task;
        }
        finally
        {
            ConfirmChannel.Pending.TryRemove(id, out _);
        }
    };

    try
    {
        var answer = await agentLoop.RunAsync(req.Message, ctx.RequestAborted);
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { kind = "answer", delta = answer })}\n\n");
    }
    catch (OperationCanceledException) { }
    catch (DeepSeekAuthException ex)
    {
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { kind = "error", delta = ex.Message })}\n\n");
    }
    catch (Exception ex)
    {
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { kind = "error", delta = ex.Message })}\n\n");
    }
    finally
    {
        // Auto-save the current session after every completed turn
        if (currentSession != null && agentLoop != null)
        {
            try
            {
                currentSession = currentSession with
                {
                    Messages = agentLoop.History.ToList(),
                    RoundUsages = agentLoop.RoundUsages.ToList(),
                    Header = currentSession.Header with
                    {
                        UpdatedAt = DateTimeOffset.Now.ToString("O"),
                    },
                };
                if (SelectedDevice() is { } selectedDevice)
                    SessionManager.SaveSession(selectedDevice, currentSession);
            }
            catch (Exception saveEx)
            {
                Log($"session auto-save failed: {saveEx.Message}");
            }
        }

        agentLoop!.Progress -= OnProgress;
        agentLoop!.StreamDelta -= OnStreamDelta;
        ConfirmChannel.Handler = null;
        await ctx.Response.WriteAsync("data: [DONE]\n\n");
    }
});

// Confirm tool
app.MapPost("/api/chat/confirm/{id}", (string id, ConfirmRequest req) =>
{
    if (ConfirmChannel.Pending.TryGetValue(id, out var tcs))
    {
        var decision = req.Decision switch
        {
            "allowOnce" => ToolConfirmation.AllowOnce,
            "allowSession" => ToolConfirmation.AllowSession,
            _ => ToolConfirmation.Deny,
        };
        return tcs.TrySetResult(decision)
            ? Results.Ok(new { status = "confirmed" })
            : Results.Ok(new { status = "already_resolved" });
    }
    return Results.NotFound(new { error = "Confirmation request not found or expired." });
});

// Save API key
app.MapPost("/api/config/key", (SaveKeyRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Key))
        return Results.BadRequest(new { error = "API key must not be empty." });

    SaveApiKey(req.Key);

    config = LoadConfig();
    CreateLoop(config);

    return Results.Ok(new { status = "key saved", chatReady = agentLoop != null });
});

// Check key status
app.MapGet("/api/config/key/status", () =>
    Results.Ok(new { configured = config.HasDeepSeekApiKey }));

// Save chat settings
app.MapPost("/api/config/settings", (SaveSettingsRequest req) =>
{
    SaveChatSettings(req.Model, req.ThinkingEnabled, req.ReasoningEffort, req.Temperature, req.TopP);
    config = LoadConfig();
    CreateLoop(config);
    return Results.Ok(new { status = "saved" });
});

// Get project info — from live TIA if connected, otherwise from local metadata
app.MapGet("/api/project/info", async () =>
{
    // 1. If TIA is connected, use live data
    var active = ActiveConnection();
    if (active != null)
    {
        try
        {
            var info = await host.Engineering.CallAsync<ProjectInfo>("get_project_info", new { });
            return Results.Ok(info);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: 502);
        }
    }

    // 2. If a workbench project is selected, read from local device metadata
    if (_selectedProjectName != null)
    {
        var exportRoot = AssistantPaths.ResolveExportRoot(_selectedProjectName);
        if (!Directory.Exists(exportRoot))
            return Results.NotFound(new { error = "Project export root not found." });

        var (deviceNames, totalComponents, lastExportUtc, _) = ReadDeviceManifests(exportRoot);

        // Count only blocks (OB/FB/FC/DB), not tags/UDTs
        var blockCount = 0;
        foreach (var dev in deviceNames)
        {
            var metaPath = Path.Combine(exportRoot, dev, "metadata.json");
            if (!File.Exists(metaPath)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var comp in comps.EnumerateArray())
                    {
                        var cat = comp.TryGetProperty("category", out var c) ? c.GetString() : null;
                        if (cat is "OB" or "FB" or "FC" or "DB") blockCount++;
                    }
                }
            }
            catch { /* skip */ }
        }

        return Results.Ok(new
        {
            name = _selectedProjectName,
            path = exportRoot,
            lastModified = lastExportUtc ?? "",
            blockCount,
            plcDevices = deviceNames,
        });
    }

    // 3. Neither connected nor selected
    return Results.BadRequest(new { error = "No project selected and no TIA connection." });
});

// List PLC blocks — from live TIA if connected, otherwise from local metadata
app.MapGet("/api/blocks", async (string? plcName) =>
{
    // 1. If TIA is connected, use live data
    var active = ActiveConnection();
    if (active != null)
    {
        try
        {
            var blocks = await host.Engineering.CallAsync<BlockInfo[]>("list_blocks",
                plcName != null ? new { plcName } : new { });
            return Results.Ok(blocks);
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message, statusCode: 502);
        }
    }

    // 2. If a workbench project is selected, read from local device metadata
    if (_selectedProjectName != null)
    {
        var exportRoot = AssistantPaths.ResolveExportRoot(_selectedProjectName);
        if (!Directory.Exists(exportRoot))
            return Results.Ok(Array.Empty<object>());

        try
        {
            var blocks = new List<object>();

            // Helper to read block entries from a single device metadata.json
            void ReadBlocksFromManifest(string metaDir)
            {
                var metaPath = Path.Combine(metaDir, "metadata.json");
                if (!File.Exists(metaPath)) return;

                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var root = doc.RootElement;
                if (!root.TryGetProperty("components", out var comps) || comps.ValueKind != JsonValueKind.Array) return;

                foreach (var comp in comps.EnumerateArray())
                {
                    var category = comp.TryGetProperty("category", out var cat) ? cat.GetString() : null;
                    if (category is not "OB" and not "FB" and not "FC" and not "DB") continue;

                    var name = comp.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var lang = comp.TryGetProperty("programmingLanguage", out var l) ? l.GetString() : null;
                    var number = comp.TryGetProperty("number", out var num) && num.TryGetInt32(out var nv) ? nv : 0;
                    var sourcePath = comp.TryGetProperty("sourcePath", out var sp) ? sp.GetString() : null;

                    blocks.Add(new
                    {
                        name,
                        blockType = category,
                        programmingLanguage = lang ?? "Unknown",
                        number,
                        groupPath = sourcePath,
                    });
                }
            }

            if (!string.IsNullOrEmpty(plcName))
            {
                // Requested a specific device folder
                var deviceDir = Path.Combine(exportRoot, plcName);
                if (Directory.Exists(deviceDir))
                {
                    ReadBlocksFromManifest(deviceDir);
                }
                else
                {
                    // Fallback: maybe it's a legacy flat export — check root
                    ReadBlocksFromManifest(exportRoot);
                }
            }
            else
            {
                // No specific device: read from all device subfolders
                var (deviceNames, _, _, deviceManifests) = ReadDeviceManifests(exportRoot);
                foreach (var dev in deviceManifests)
                {
                    ReadBlocksFromManifest(dev.ExportRoot);
                }
            }

            return Results.Ok(blocks);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Failed to read blocks from metadata: {ex.Message}", statusCode: 502);
        }
    }

    // 3. Neither connected nor selected
    return Results.BadRequest(new { error = "No project selected and no TIA connection." });
});

// Block source code (from knowledge DB — no TIA connection required)
app.MapGet("/api/blocks/{blockName}/source-code", async (string blockName, string? plcName) =>
{
    // Resolve export root: active connection → selected project → error
    string? projectName = ActiveConnection()?.ProjectName ?? _selectedProjectName;
    if (projectName == null)
        return Results.BadRequest(new { error = "No project selected and no TIA connection." });

    var dbPath = AssistantPaths.ResolveKnowledgeDbPath(projectName);
    if (!File.Exists(dbPath))
    {
        return Results.Ok(new
        {
            exists = false,
            message = "Knowledge database not found. Run ingest_source on the export root to build it.",
            dbPath,
        });
    }

    try
    {
        var raw = await host.Knowledge.CallAsync<JsonElement>("get_block",
            new { dbPath, block = blockName });
        // Flatten: extract block + networks from the MCP result to the top level
        JsonElement? block = raw.TryGetProperty("block", out var b) ? b : null;
        JsonElement? networks = raw.TryGetProperty("networks", out var n) ? n : null;
        return Results.Ok(new { exists = true, block, networks });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

/* ── Knowledge graph browser endpoints ─────────────────── */

static async Task<IResult> ExecuteKnowledgeQuery(McpHost host, string dbPath, string sql, Func<JsonElement, IResult> onResult)
{
    try
    {
        var raw = await host.Knowledge.CallAsync<JsonElement>("query", new { dbPath, sql });
        return onResult(raw);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 502);
    }
}

// List distinct node kinds for combo box filter
app.MapGet("/api/knowledge/node-kinds", async (string projectName) =>
{
    var dbPath = AssistantPaths.ResolveKnowledgeDbPath(projectName);
    if (!File.Exists(dbPath)) return Results.NotFound(new { error = "Knowledge database not found.", dbPath });

    return await ExecuteKnowledgeQuery(host, dbPath,
        "SELECT DISTINCT kind FROM graph_nodes ORDER BY kind;",
        raw => Results.Ok(new { kinds = EnumerateColumn(raw, "kind") }));
});

// List nodes, optionally filtered by kind
app.MapGet("/api/knowledge/nodes", async (string projectName, string? kind) =>
{
    var dbPath = AssistantPaths.ResolveKnowledgeDbPath(projectName);
    if (!File.Exists(dbPath)) return Results.NotFound(new { error = "Knowledge database not found.", dbPath });

    var sql = string.IsNullOrWhiteSpace(kind)
        ? "SELECT id, kind, name FROM graph_nodes ORDER BY kind, name LIMIT 2000;"
        : $"SELECT id, kind, name FROM graph_nodes WHERE kind = '{kind.Replace("'", "''")}' ORDER BY kind, name LIMIT 2000;";
    return await ExecuteKnowledgeQuery(host, dbPath, sql,
        raw => Results.Ok(new { nodes = EnumerateRows(raw, r => new { id = r["id"].GetString() ?? "", kind = r["kind"].GetString() ?? "", name = r["name"].GetString() ?? "" }) }));
});

// List distinct edge types for combo box filter
app.MapGet("/api/knowledge/edge-types", async (string projectName) =>
{
    var dbPath = AssistantPaths.ResolveKnowledgeDbPath(projectName);
    if (!File.Exists(dbPath)) return Results.NotFound(new { error = "Knowledge database not found.", dbPath });

    return await ExecuteKnowledgeQuery(host, dbPath,
        "SELECT DISTINCT type FROM graph_edges ORDER BY type;",
        raw => Results.Ok(new { types = EnumerateColumn(raw, "type") }));
});

// List edges, filtered by from_node_id and optionally by type
app.MapGet("/api/knowledge/edges", async (string projectName, string fromNodeId, string? type) =>
{
    var dbPath = AssistantPaths.ResolveKnowledgeDbPath(projectName);
    if (!File.Exists(dbPath)) return Results.NotFound(new { error = "Knowledge database not found.", dbPath });

    var safeFrom = fromNodeId.Replace("'", "''");
    var typeFilter = string.IsNullOrWhiteSpace(type)
        ? string.Empty
        : $" AND type = '{type.Replace("'", "''")}'";
    var sql = $"SELECT id, from_node_id, to_node_id, type FROM graph_edges WHERE from_node_id = '{safeFrom}'{typeFilter} ORDER BY type, id LIMIT 2000;";
    return await ExecuteKnowledgeQuery(host, dbPath, sql,
        raw => Results.Ok(new { edges = EnumerateRows(raw, r => new { id = r["id"].GetString() ?? "", from_node_id = r["from_node_id"].GetString() ?? "", to_node_id = r["to_node_id"].GetString() ?? "", type = r["type"].GetString() ?? "" }) }));
});

// Get properties for a node
app.MapGet("/api/knowledge/node-properties", async (string projectName, string nodeId) =>
{
    var dbPath = AssistantPaths.ResolveKnowledgeDbPath(projectName);
    if (!File.Exists(dbPath)) return Results.NotFound(new { error = "Knowledge database not found.", dbPath });

    var safeId = nodeId.Replace("'", "''");
    var sql = $"SELECT name, value FROM graph_node_properties WHERE node_id = '{safeId}' ORDER BY name;";
    return await ExecuteKnowledgeQuery(host, dbPath, sql,
        raw => Results.Ok(new { properties = EnumerateRows(raw, r => new { name = r["name"].GetString() ?? "", value = r["value"].GetString() ?? "" }) }));
});

// Get properties for an edge
app.MapGet("/api/knowledge/edge-properties", async (string projectName, string edgeId) =>
{
    var dbPath = AssistantPaths.ResolveKnowledgeDbPath(projectName);
    if (!File.Exists(dbPath)) return Results.NotFound(new { error = "Knowledge database not found.", dbPath });

    var safeId = edgeId.Replace("'", "''");
    var sql = $"SELECT name, value FROM graph_edge_properties WHERE edge_id = '{safeId}' ORDER BY name;";
    return await ExecuteKnowledgeQuery(host, dbPath, sql,
        raw => Results.Ok(new { properties = EnumerateRows(raw, r => new { name = r["name"].GetString() ?? "", value = r["value"].GetString() ?? "" }) }));
});

// ── JSON helpers for knowledge query results ─────────────

static string[] EnumerateColumn(JsonElement raw, string column)
{
    if (!raw.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
        return Array.Empty<string>();
    if (!raw.TryGetProperty("columns", out var cols) || cols.ValueKind != JsonValueKind.Array)
        return Array.Empty<string>();

    var colIndex = -1;
    for (var i = 0; i < cols.GetArrayLength(); i++)
    {
        if (cols[i].GetString() == column) { colIndex = i; break; }
    }
    if (colIndex < 0) return Array.Empty<string>();

    var results = new List<string>();
    foreach (var row in rows.EnumerateArray())
    {
        var value = row[colIndex];
        if (value.ValueKind == JsonValueKind.String) results.Add(value.GetString()!);
    }
    return results.ToArray();
}

static T[] EnumerateRows<T>(JsonElement raw, Func<Dictionary<string, JsonElement>, T> map)
{
    if (!raw.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
        return Array.Empty<T>();
    if (!raw.TryGetProperty("columns", out var cols) || cols.ValueKind != JsonValueKind.Array)
        return Array.Empty<T>();

    var colNames = new string[cols.GetArrayLength()];
    for (var i = 0; i < colNames.Length; i++) colNames[i] = cols[i].GetString() ?? $"c{i}";

    var results = new List<T>();
    foreach (var row in rows.EnumerateArray())
    {
        var dict = new Dictionary<string, JsonElement>();
        for (var i = 0; i < row.GetArrayLength() && i < colNames.Length; i++)
            dict[colNames[i]] = row[i];
        results.Add(map(dict));
    }
    return results.ToArray();
}

// Chat history
app.MapGet("/api/chat/history", () =>
{
    if (agentLoop == null) return Results.Ok(Array.Empty<object>());
    return Results.Ok(agentLoop.History.Select(m => new
    {
        role = m.Role,
        content = m.Content,
        toolCallId = m.ToolCallId,
        timestamp = m.Timestamp,
    }));
});

// Clear chat history
app.MapPost("/api/chat/clear", () =>
{
    agentLoop?.ClearHistory();
    return Results.Ok(new { status = "cleared" });
});

/* ── Session management endpoints ─────────────────────── */

// List sessions for a project (lightweight metadata only)
app.MapGet("/api/chat/sessions", (string projectName) =>
{
    if (SelectedDevice() is not { } selectedDevice)
        return Results.BadRequest(new { error = "Select a workbench device first." });
    try
    {
        var sessions = SessionManager.ListSessions(selectedDevice);
        return Results.Ok(sessions);
    }
    catch (Exception ex)
    {
        Log($"list sessions error: {ex.Message}");
        return Results.Ok(Array.Empty<object>());
    }
});

// Create a new session
app.MapPost("/api/chat/session/new", (NewSessionRequest? req) =>
{
    if (SelectedDevice() is not { } selectedDevice)
        return Results.BadRequest(new { error = "No workbench device selected." });

    if (agentLoop == null)
        return Results.BadRequest(new { error = "Chat not ready. Configure your DeepSeek API key first." });

    var runtimeContext = BuildRuntimeContext();
    currentSession = SessionManager.CreateNewSession(selectedDevice, agentLoop.Settings, runtimeContext);
    agentLoop.ClearHistory();
    agentLoop.SessionId = currentSession.Header.SessionId;
    agentLoop.ProjectName = selectedDevice.DeviceId;

    Log($"session created: {currentSession.Header.SessionId} for device '{selectedDevice.DeviceId}'");
    return Results.Ok(new
    {
        sessionId = currentSession.Header.SessionId,
        createdAt = currentSession.Header.CreatedAt,
    });
});

// Load an existing session
app.MapPost("/api/chat/session/load", (LoadSessionRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.ProjectName))
        return Results.BadRequest(new { error = "projectName is required." });
    if (string.IsNullOrWhiteSpace(req.SessionId))
        return Results.BadRequest(new { error = "sessionId is required." });

    if (agentLoop == null)
        return Results.BadRequest(new { error = "Chat not ready. Configure your DeepSeek API key first." });

    if (SelectedDevice() is not { } selectedDevice)
        return Results.BadRequest(new { error = "No workbench device selected." });
    var loaded = SessionManager.LoadSession(selectedDevice, req.SessionId);
    if (loaded == null)
        return Results.NotFound(new { error = "Session not found or corrupted." });

    currentSession = loaded;
    agentLoop.RestoreFrom(loaded.Messages, loaded.RoundUsages);
    agentLoop.SessionId = loaded.Header.SessionId;
    agentLoop.ProjectName = loaded.Header.ProjectName;

    Log($"session loaded: {loaded.Header.SessionId} ({loaded.Messages.Count} messages, {loaded.Header.ProjectName})");
    return Results.Ok(new
    {
        sessionId = loaded.Header.SessionId,
        messageCount = loaded.Messages.Count,
    });
});

// Delete a session
app.MapPost("/api/chat/session/delete", (DeleteSessionRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.ProjectName))
        return Results.BadRequest(new { error = "projectName is required." });
    if (string.IsNullOrWhiteSpace(req.SessionId))
        return Results.BadRequest(new { error = "sessionId is required." });

    if (currentSession?.Header.SessionId == req.SessionId)
        return Results.BadRequest(new { error = "Cannot delete the active session. Create or load a different session first." });

    SessionManager.DeleteSession(req.ProjectName, req.SessionId);
    Log($"session deleted: {req.SessionId}");
    return Results.Ok(new { status = "deleted" });
});

// Get active session info
app.MapGet("/api/chat/session/info", () =>
{
    if (currentSession == null)
        return Results.Ok(new { active = false });

    return Results.Ok(new
    {
        active = true,
        sessionId = currentSession.Header.SessionId,
        projectName = currentSession.Header.ProjectName,
        createdAt = currentSession.Header.CreatedAt,
        updatedAt = currentSession.Header.UpdatedAt,
        messageCount = currentSession.Header.ProjectName is not null
            ? currentSession.Messages.Count(m => m.Role != "system")
            : 0,
    });
});

// List exported projects
app.MapGet("/api/projects", () =>
{
    var exportsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlcAiAssistant", "exports");

    if (!Directory.Exists(exportsDir))
        return Results.Ok(Array.Empty<ExportedProject>());

    var projects = new List<ExportedProject>();
    foreach (var subDir in Directory.EnumerateDirectories(exportsDir).Order())
    {
        var name = Path.GetFileName(subDir);

        // Read per-device manifests under this project folder
        var (deviceNames, totalComponents, lastExportUtc, _) = ReadDeviceManifests(subDir);

        // Also read plcDevices from project metadata (authoritative after rebuild_export / sync_export).
        var metaDeviceNames = ReadProjectMetaStringArray(subDir, "plcDevices");
        if (metaDeviceNames.Length > 0)
        {
            // Merge: project metadata is authoritative, but also include any device subfolder
            // that exists on disk (may have been left by a partial export).
            var merged = new HashSet<string>(metaDeviceNames, StringComparer.OrdinalIgnoreCase);
            foreach (var d in deviceNames) merged.Add(d);
            deviceNames = merged.OrderBy(d => d).ToArray();
        }

        // Read sourceProjectPath and plcSoftwareChecksum from project-level metadata.json
        // at the export root (new structure since 2026-07-24). Fall back to reading from
        // the first device subfolder or legacy top-level manifest for pre-migration exports.
        string? sourceProjectPath = ReadProjectMetaString(subDir, "sourceProjectPath");
        string? plcSoftwareChecksum = ReadProjectMetaPlcChecksum(subDir);

        // Legacy fallback: pre-migration device manifests or flat-export root metadata
        if (string.IsNullOrEmpty(sourceProjectPath))
        {
            foreach (var devDir in Directory.EnumerateDirectories(subDir))
            {
                var devMeta = Path.Combine(devDir, "metadata.json");
                if (!File.Exists(devMeta)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(devMeta));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("sourceProjectPath", out var sp) && sp.ValueKind == JsonValueKind.String)
                    {
                        sourceProjectPath = sp.GetString();
                        if (plcSoftwareChecksum is null
                            && root.TryGetProperty("plcSoftwareChecksum", out var chk) && chk.ValueKind == JsonValueKind.String)
                            plcSoftwareChecksum = chk.GetString();
                        break;
                    }
                }
                catch { /* skip unparseable metadata */ }
            }
        }
        if (string.IsNullOrEmpty(sourceProjectPath))
        {
            var rootMeta = Path.Combine(subDir, "metadata.json");
            if (File.Exists(rootMeta))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(rootMeta));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("sourceProjectPath", out var sp) && sp.ValueKind == JsonValueKind.String)
                        sourceProjectPath = sp.GetString();
                    if (plcSoftwareChecksum is null
                        && root.TryGetProperty("plcSoftwareChecksum", out var chk) && chk.ValueKind == JsonValueKind.String)
                        plcSoftwareChecksum = chk.GetString();
                }
                catch { }
            }
        }

        var isConnected = _activeConnectionId != null
            && _connections.TryGetValue(_activeConnectionId, out var activeEntry)
            && string.Equals(activeEntry.ProjectName, name, StringComparison.Ordinal);

        projects.Add(new ExportedProject(
            Name: name,
            ExportRoot: subDir,
            SourceProjectPath: sourceProjectPath,
            PlcSoftwareChecksum: plcSoftwareChecksum,
            ComponentCount: totalComponents,
            LastExportUtc: lastExportUtc,
            Connected: isConnected,
            PlcDevices: deviceNames
        ));
    }

    return Results.Ok(projects);
});

// Select a workbench project for offline viewing (no TIA connection required).
app.MapPost("/api/projects/select", (SelectProjectRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.ProjectName))
        return Results.BadRequest(new { error = "Project name is required." });

    var exportRoot = AssistantPaths.ResolveExportRoot(req.ProjectName);
    if (!Directory.Exists(exportRoot))
        return Results.NotFound(new { error = $"Project '{req.ProjectName}' not found in exports directory." });

    _selectedProjectName = req.ProjectName;
    Log($"project selected for offline viewing: {req.ProjectName}");
    return Results.Ok(new { selected = req.ProjectName });
});

// Deselect the currently selected workbench project.
app.MapPost("/api/projects/deselect", () =>
{
    _selectedProjectName = null;
    return Results.Ok(new { status = "deselected" });
});

// Create a new project from a running TIA session
app.MapPost("/api/projects/create", async (CreateProjectRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.ProjectName))
        return Results.BadRequest(new { error = "Project name is required." });

    var exportRoot = AssistantPaths.ResolveExportRoot(req.ProjectName);

    if (Directory.Exists(exportRoot))
        return Results.Conflict(new { error = $"Project '{req.ProjectName}' already exists. Choose a different name." });

    try
    {
        Log($"creating project '{req.ProjectName}' from session {req.SessionId}");
        var info = await host.Engineering.CallAsync<EngConnectionInfo>("connect", new { sessionId = req.SessionId });
        Directory.CreateDirectory(exportRoot);

        Log($"exporting blocks to {exportRoot}");
        await host.Engineering.CallAsync<JsonElement>("export_all_blocks", new { outputDir = exportRoot });

        // Init git repo in the export root (non-fatal)
        try
        {
            if (host.VersionControl != null)
            {
                Log($"initialising git repo at {exportRoot}");
                await host.VersionControl.CallAsync<JsonElement>("vc_init", new { repoPath = exportRoot });
            }
        }
        catch (Exception vcEx)
        {
            Log($"git init warning (non-fatal): {vcEx.Message}");
        }

        try
        {
            Log($"ingesting source into knowledge DB");
            await host.Knowledge.CallAsync<JsonElement>("ingest_source", new { exportRoot });
        }
        catch (Exception ingestEx)
        {
            Log($"knowledge ingest warning (non-fatal): {ingestEx.Message}");
        }

        RecordSourcePath(req.ProjectName, info.ProjectPath ?? req.ProjectPath ?? "");

        var id = ConnectionEntry.NewId();
        var entry = new ConnectionEntry(id, req.SessionId.ToString(), req.ProjectName, info.ProjectPath, info.Attached, null);
        _connections[id] = entry;
        _activeConnectionId = id;

        // Read checksum from project-level metadata.json (written by ExportAllBlocksForPlc).
        string? checksum = ReadProjectMetaPlcChecksum(exportRoot);
        var (deviceNames, totalComponents, lastExportUtc, _) = ReadDeviceManifests(exportRoot);

        Log($"project created: {req.ProjectName}");
        return Results.Ok(new ExportedProject(
            req.ProjectName,
            exportRoot,
            req.ProjectPath ?? info.ProjectPath,
            checksum,
            totalComponents,
            lastExportUtc ?? DateTimeOffset.UtcNow.ToString("O"),
            Connected: true,
            deviceNames
        ));
    }
    catch (ToolCallException ex)
    {
        Log($"project create ERROR: [{ex.Code}] {ex.Message}");
        return Results.Problem($"[{ex.Code}] {ex.Message}", statusCode: 502);
    }
    catch (Exception ex)
    {
        Log($"project create ERROR: {ex.Message}");
        return Results.Problem(ex.Message, statusCode: 502);
    }
});

// Version control: status
app.MapGet("/api/vc/status", async () =>
{
    var repoPath = ResolveRepoPath();
    if (repoPath == null) return Results.BadRequest(new { error = "No project selected or connected." });
    if (host.VersionControl == null) return Results.BadRequest(new { error = "Version control server not available." });
    if (!Directory.Exists(Path.Combine(repoPath, ".git")))
        return Results.Ok(new { result = new { repoPath, branch = "no-repo", entries = Array.Empty<object>() } });

    try
    {
        var result = await host.VersionControl.CallAsync<JsonElement>("vc_status", new { repoPath });
        return Results.Ok(new { result });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { result = new { repoPath, branch = "error", entries = Array.Empty<object>(), _error = ex.Message } });
    }
});

// Version control: log
app.MapGet("/api/vc/log", async (int? maxCount, string? filePath) =>
{
    var repoPath = ResolveRepoPath();
    if (repoPath == null) return Results.BadRequest(new { error = "No project selected or connected." });
    if (host.VersionControl == null) return Results.BadRequest(new { error = "Version control server not available." });
    if (!Directory.Exists(Path.Combine(repoPath, ".git")))
        return Results.Ok(new { result = new { repoPath, commits = Array.Empty<object>() } });

    try
    {
        var args = new Dictionary<string, object?> { ["repoPath"] = repoPath };
        if (maxCount.HasValue) args["maxCount"] = maxCount.Value;
        if (filePath != null) args["filePath"] = filePath;
        var result = await host.VersionControl.CallAsync<JsonElement>("vc_log", args);
        return Results.Ok(new { result });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { result = new { repoPath, commits = Array.Empty<object>(), _error = ex.Message } });
    }
});

// Version control: diff
app.MapGet("/api/vc/diff", async (string filePath, string? oldSha, string? newSha) =>
{
    var repoPath = ResolveRepoPath();
    if (repoPath == null) return Results.BadRequest(new { error = "No project selected or connected." });
    if (host.VersionControl == null) return Results.BadRequest(new { error = "Version control server not available." });
    if (!Directory.Exists(Path.Combine(repoPath, ".git")))
        return Results.Ok(new { result = new { repoPath, filePath, hunks = Array.Empty<object>(), binary = false } });

    try
    {
        var args = new Dictionary<string, object?> { ["repoPath"] = repoPath, ["filePath"] = filePath };
        if (oldSha != null) args["oldSha"] = oldSha;
        if (newSha != null) args["newSha"] = newSha;
        var result = await host.VersionControl.CallAsync<JsonElement>("vc_diff", args);
        return Results.Ok(new { result });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { result = new { repoPath, filePath, hunks = Array.Empty<object>(), binary = false, _error = ex.Message } });
    }
});

// Version control: stage files (POST)
app.MapPost("/api/vc/add", async (VcAddRequest body) =>
{
    var repoPath = ResolveRepoPath();
    if (repoPath == null) return Results.BadRequest(new { error = "No project selected or connected." });
    if (host.VersionControl == null) return Results.BadRequest(new { error = "Version control server not available." });
    if (!Directory.Exists(Path.Combine(repoPath, ".git")))
        return Results.BadRequest(new { error = "Not a git repository." });

    try
    {
        var args = new Dictionary<string, object?> { ["repoPath"] = repoPath };
        if (body.Paths is { Length: > 0 }) args["paths"] = body.Paths;
        var result = await host.VersionControl.CallAsync<JsonElement>("vc_add", args);
        return Results.Ok(new { result });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Version control: commit (POST)
app.MapPost("/api/vc/commit", async (VcCommitRequest body) =>
{
    var repoPath = ResolveRepoPath();
    if (repoPath == null) return Results.BadRequest(new { error = "No project selected or connected." });
    if (host.VersionControl == null) return Results.BadRequest(new { error = "Version control server not available." });
    if (!Directory.Exists(Path.Combine(repoPath, ".git")))
        return Results.BadRequest(new { error = "Not a git repository." });

    try
    {
        var args = new Dictionary<string, object?> { ["repoPath"] = repoPath };
        if (!string.IsNullOrWhiteSpace(body.Message)) args["message"] = body.Message;
        var result = await host.VersionControl.CallAsync<JsonElement>("vc_commit", args);
        return Results.Ok(new { result });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Version control: restore (POST)
app.MapPost("/api/vc/restore", async (VcRestoreRequest body) =>
{
    var repoPath = ResolveRepoPath();
    if (repoPath == null) return Results.BadRequest(new { error = "No project selected or connected." });
    if (host.VersionControl == null) return Results.BadRequest(new { error = "Version control server not available." });
    if (!Directory.Exists(Path.Combine(repoPath, ".git")))
        return Results.BadRequest(new { error = "Not a git repository." });

    // Confirm destructive action
    if (body.ConfirmId == null || body.Decision == null)
        return Results.Ok(new { confirm = new { message = $"Restore discards working-tree changes{(body.FilePath != null ? $" in {body.FilePath}" : " in ALL files")}. Proceed?" } });

    if (body.Decision != "approved")
        return Results.BadRequest(new { error = "Restore rejected by user." });

    try
    {
        var args = new Dictionary<string, object?> { ["repoPath"] = repoPath };
        if (!string.IsNullOrWhiteSpace(body.FilePath)) args["filePath"] = body.FilePath;
        if (!string.IsNullOrWhiteSpace(body.SourceSha)) args["sourceSha"] = body.SourceSha;
        var result = await host.VersionControl.CallAsync<JsonElement>("vc_restore", args);
        return Results.Ok(new { result });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Version control: branches (GET)
app.MapGet("/api/vc/branches", async (string? projectName) =>
{
    string? repoPath;
    if (!string.IsNullOrWhiteSpace(projectName))
    {
        repoPath = AssistantPaths.ResolveExportRoot(projectName);
        if (!Directory.Exists(repoPath))
            return Results.BadRequest(new { error = $"Project '{projectName}' not found." });
    }
    else
    {
        repoPath = ResolveRepoPath();
    }

    if (repoPath == null) return Results.BadRequest(new { error = "No project selected or connected." });
    if (host.VersionControl == null) return Results.BadRequest(new { error = "Version control server not available." });
    if (!Directory.Exists(Path.Combine(repoPath, ".git")))
        return Results.Ok(new { result = new { branches = Array.Empty<object>() } });

    try
    {
        var result = await host.VersionControl.CallAsync<JsonElement>("vc_branches", new { repoPath });
        return Results.Ok(new { result });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { result = new { branches = Array.Empty<object>() }, _error = ex.Message });
    }
});

// Version control: checkout (POST)
app.MapPost("/api/vc/checkout", async (VcCheckoutRequest body) =>
{
    var repoPath = ResolveRepoPath();
    if (repoPath == null) return Results.BadRequest(new { error = "No project selected or connected." });
    if (host.VersionControl == null) return Results.BadRequest(new { error = "Version control server not available." });
    if (!Directory.Exists(Path.Combine(repoPath, ".git")))
        return Results.BadRequest(new { error = "Not a git repository." });

    try
    {
        var result = await host.VersionControl.CallAsync<JsonElement>("vc_checkout", new { repoPath, branchName = body.BranchName });
        return Results.Ok(new { result });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Version control: init (POST)
app.MapPost("/api/vc/init", async (VcInitRequest body) =>
{
    var repoPath = AssistantPaths.ResolveExportRoot(body.ProjectName);
    if (!Directory.Exists(repoPath))
        return Results.BadRequest(new { error = $"Project '{body.ProjectName}' not found." });
    if (host.VersionControl == null) return Results.BadRequest(new { error = "Version control server not available." });

    try
    {
        var result = await host.VersionControl.CallAsync<JsonElement>("vc_init", new { repoPath });

        // Stage non-.db files and create an initial commit so the repo has a branch (master)
        // visible in the UI.  Exclude .db files to avoid tracking the knowledge database
        // (it's rebuilt on ingest and should be in .gitignore).
        try
        {
            var files = Directory.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                         && !f.Contains("\\.git", StringComparison.OrdinalIgnoreCase)
                         && !f.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
                         && !f.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(repoPath, f).Replace('\\', '/'))
                .ToArray();

            if (files.Length > 0)
            {
                await host.VersionControl.CallAsync<JsonElement>("vc_add", new { repoPath, paths = files });
                await host.VersionControl.CallAsync<JsonElement>("vc_commit", new { repoPath, message = "Initial commit" });
                Log($"initial commit created for {body.ProjectName} — {files.Length} files");
            }
        }
        catch (Exception inner)
        {
            Log($"initial commit warning (non-fatal): {inner.Message}");
        }

        return Results.Ok(new { result });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

/* ── Static files (production) ──────────────────────── */
var studioDist = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "studio", "dist");
if (Directory.Exists(studioDist))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}

app.Run();

/* ── Device manifest helpers ─────────────────────────── */

/// <summary>Reads device subfolder manifests under an export root.
/// Returns device names, aggregate component count, latest export timestamp,
/// and per-device metadata for the calling endpoints.
/// Falls back to treating the export root itself as a single (legacy) device
/// when no per-device subfolders exist.</summary>
static (string[] DeviceNames, int TotalComponents, string? LastExportUtc, List<DeviceManifest> Devices)
    ReadDeviceManifests(string exportRoot)
{
    var deviceNames = new List<string>();
    var totalComponents = 0;
    string? latestExportUtc = null;
    var devices = new List<DeviceManifest>();

    // New structure: subdirectories each with their own metadata.json
    bool foundDeviceFolder = false;
    foreach (var subDir in Directory.EnumerateDirectories(exportRoot))
    {
        var metaPath = Path.Combine(subDir, "metadata.json");
        if (!File.Exists(metaPath)) continue;

        foundDeviceFolder = true;
        var deviceName = Path.GetFileName(subDir);
        deviceNames.Add(deviceName);

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var root = doc.RootElement;

            var componentCount = 0;
            string? exportFinished = null;

            if (root.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Array)
                componentCount = comps.GetArrayLength();
            if (root.TryGetProperty("exportFinishedUtc", out var utc) && utc.ValueKind == JsonValueKind.String)
                exportFinished = utc.GetString();

            totalComponents += componentCount;
            if (exportFinished is not null && (latestExportUtc is null || string.Compare(exportFinished, latestExportUtc, StringComparison.Ordinal) > 0))
                latestExportUtc = exportFinished;

            devices.Add(new DeviceManifest(deviceName, subDir, componentCount, exportFinished));
        }
        catch { /* skip unparseable device metadata */ }
    }

    // Legacy fallback: flat export with a single top-level metadata.json
    if (!foundDeviceFolder)
    {
        var legacyMeta = Path.Combine(exportRoot, "metadata.json");
        if (File.Exists(legacyMeta))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(legacyMeta));
                var root = doc.RootElement;
                var componentCount = 0;
                string? exportFinished = null;

                if (root.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Array)
                    componentCount = comps.GetArrayLength();
                if (root.TryGetProperty("exportFinishedUtc", out var utc) && utc.ValueKind == JsonValueKind.String)
                    exportFinished = utc.GetString();

                totalComponents += componentCount;
                if (exportFinished is not null && (latestExportUtc is null || string.Compare(exportFinished, latestExportUtc, StringComparison.Ordinal) > 0))
                    latestExportUtc = exportFinished;

                deviceNames.Add("<legacy>");
                devices.Add(new DeviceManifest("<legacy>", exportRoot, componentCount, exportFinished));
            }
            catch { /* skip unparseable legacy metadata */ }
        }
    }

    return (deviceNames.ToArray(), totalComponents, latestExportUtc, devices);
}

/// <summary>Read a string array property from the project-level metadata.json at <paramref name="exportRoot"/>.</summary>
static string[] ReadProjectMetaStringArray(string exportRoot, string propertyName)
{
    var metaPath = Path.Combine(exportRoot, "metadata.json");
    if (!File.Exists(metaPath)) return Array.Empty<string>();
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
        var root = doc.RootElement;
        if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            var result = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    result.Add(item.GetString()!);
            }
            return result.ToArray();
        }
        return Array.Empty<string>();
    }
    catch { return Array.Empty<string>(); }
}

/// <summary>Read a string property from the project-level metadata.json at <paramref name="exportRoot"/>.</summary>
static string? ReadProjectMetaString(string exportRoot, string propertyName)
{
    var metaPath = Path.Combine(exportRoot, "metadata.json");
    if (!File.Exists(metaPath)) return null;
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
        var root = doc.RootElement;
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
    catch { return null; }
}

/// <summary>Read the checksum dictionary from project-level metadata.json. Returns the first
/// entry's value (common single-PLC case), or null when absent.</summary>
static string? ReadProjectMetaPlcChecksum(string exportRoot)
{
    var metaPath = Path.Combine(exportRoot, "metadata.json");
    if (!File.Exists(metaPath)) return null;
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
        var root = doc.RootElement;
        if (root.TryGetProperty("plcSoftwareChecksum", out var chk))
        {
            if (chk.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in chk.EnumerateObject())
                {
                    if (kv.Value.ValueKind == JsonValueKind.String)
                        return kv.Value.GetString();
                    break; // first entry only
                }
            }
            else if (chk.ValueKind == JsonValueKind.String)
            {
                return chk.GetString();
            }
        }
        return null;
    }
    catch { return null; }
}

sealed record DeviceManifest(string DeviceName, string ExportRoot, int ComponentCount, string? LastExportUtc);


/* ── Request types ──────────────────────────────────── */

sealed record AppConfig(
    string EngineeringServerPath,
    string KnowledgeServerPath,
    string VersionControlServerPath,
    string SourceEditorServerPath,
    string? DeepSeekApiKey = null,
    string DeepSeekModel = "deepseek-v4-flash",
    string DeepSeekBaseUrl = "https://api.deepseek.com",
    bool DeepSeekThinkingEnabled = true,
    string DeepSeekReasoningEffort = "high",
    double DeepSeekTemperature = 1.0,
    double DeepSeekTopP = 1.0)
{
    public bool HasDeepSeekApiKey => !string.IsNullOrWhiteSpace(DeepSeekApiKey);
}

record ConnectRequest(int? SessionId, string? ProjectPath, bool? WithUI, int? TimeoutSeconds);
record ChatRequest(string Message);
record SaveKeyRequest(string Key);
record SaveSettingsRequest(string Model, bool ThinkingEnabled, string ReasoningEffort, double Temperature, double TopP);
record ConfirmRequest(string Decision);
record SelectPlcRequest(string PlcName);
record ToolCallRequest(string Server, string Tool, JsonElement? Args = null, string? ConfirmId = null, string? Decision = null);
record CreateProjectRequest(int SessionId, string ProjectName, string? ProjectPath);
record SelectProjectRequest(string ProjectName);
record NewSessionRequest(string? ProjectName);
record LoadSessionRequest(string SessionId, string ProjectName);
record DeleteSessionRequest(string SessionId, string ProjectName);
record VcAddRequest(string[]? Paths);
record VcCommitRequest(string Message);
record VcRestoreRequest(string? FilePath, string? SourceSha, string? ConfirmId, string? Decision);
record VcCheckoutRequest(string BranchName);
record VcInitRequest(string ProjectName);

record ExportedProject(
    string Name,
    string ExportRoot,
    string? SourceProjectPath,
    string? PlcSoftwareChecksum,
    int ComponentCount,
    string? LastExportUtc,
    bool Connected,
    string[] PlcDevices
);

sealed record ConnectionEntry(
    string Id,
    string? SessionId,
    string ProjectName,
    string? ProjectPath,
    bool Attached,
    string? SelectedPlc)
{
    public static string NewId() => Guid.NewGuid().ToString("N");
}

/* ── Tool confirmation channel ─────────────────────── */
sealed class ConfirmChannel
{
    public static Func<ToolConfirmationRequest, Task<ToolConfirmation>>? Handler;
    public static readonly ConcurrentDictionary<string, TaskCompletionSource<ToolConfirmation>> Pending = new();
}

/* ── Log buffer (ring buffer + subscriber channels) ──── */
sealed class LogBuffer
{
    private const int MaxLines = 500;
    private readonly ConcurrentQueue<string> _ring = new();
    private readonly object _lock = new();
    private readonly List<Channel<string>> _subscribers = new();

    public void Write(string line)
    {
        lock (_lock)
        {
            _ring.Enqueue(line);
            while (_ring.Count > MaxLines) _ring.TryDequeue(out _);
            foreach (var sub in _subscribers)
                sub.Writer.TryWrite(line);
        }
    }

    public IReadOnlyList<string> Snapshot() => _ring.ToArray();

    public Channel<string> Subscribe()
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        lock (_lock) _subscribers.Add(channel);
        return channel;
    }

    public void Unsubscribe(Channel<string> channel)
    {
        lock (_lock) _subscribers.Remove(channel);
    }
}
