using System.Text.Json;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Contracts.Sandbox;

namespace ApiHost.AppAssistant;

internal sealed record AssistantConversation(
    string SessionId,
    List<ChatMessage> Messages,
    List<UsageInfo?> RoundUsages);

/// <summary>One persistent Workbench Assistant conversation, independent of UI selection.</summary>
internal sealed class WorkbenchAssistantService(
    WorkbenchApiState state,
    AppAssistantGateway gateway,
    AtomicJsonStore store,
    IServiceProvider services,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IHostEnvironment environment,
    CompatibilityRuntimeState runtimeState,
    DeviceToolArgumentBinder binder,
    PendingToolActions pending,
    SandboxPolicy policy)
{
    internal const string ConfirmationContextKey = "workbench-assistant";

    public sealed record WorkbenchOption(string WorkbenchId, string Name);
    public sealed record Turn(
        AppAssistantWorkbenchContext? Context,
        IReadOnlyList<WorkbenchOption> Workbenches,
        string Answer,
        string SessionId);

    private readonly SemaphoreSlim turnGate = new(1, 1);
    private readonly string sessionPath = environment.IsEnvironment("Testing")
        ? Path.Combine(Path.GetTempPath(), "workbench-assistant-tests", Guid.NewGuid().ToString("N"), "session.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutomationWorkbench", "assistant", "session.json");
    private AssistantConversation? session;
    private AgentLoop? loop;
    private WorkbenchSelection? turnSelection;

    public string? ActiveSessionId => session?.SessionId;

    public async Task<Turn> BootstrapAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = GetOrCreateSession();
        var context = await CurrentContextAsync().ConfigureAwait(false);
        var workbenches = Workbenches();
        return new Turn(context, workbenches, Describe(context, workbenches), current.SessionId);
    }

    public async Task<Turn> ChatAsync(
        string message,
        Action<string> progress,
        CancellationToken cancellationToken = default)
    {
        await turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = GetOrCreateSession();
            turnSelection = state.Selection;
            var active = await EnsureLoopAsync(current, cancellationToken).ConfigureAwait(false);
            void OnProgress(string line) => progress(line);
            active.Progress += OnProgress;
            string answer;
            try
            {
                answer = await active.RunAsync(message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                active.Progress -= OnProgress;
                session = current with
                {
                    Messages = active.History.ToList(),
                    RoundUsages = active.RoundUsages.ToList(),
                };
                store.Write(sessionPath, session);
            }

            var context = await CurrentContextAsync().ConfigureAwait(false);
            return new Turn(context, Workbenches(), answer, current.SessionId);
        }
        finally
        {
            turnGate.Release();
        }
    }

    private AssistantConversation GetOrCreateSession()
    {
        if (session is not null) return session;
        if (File.Exists(sessionPath))
        {
            try
            {
                session = store.Read<AssistantConversation>(sessionPath);
                if (!string.IsNullOrWhiteSpace(session.SessionId)) return session;
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                throw new AppAssistantGatewayException("ASSISTANT_SESSION_UNAVAILABLE",
                    "The saved Workbench Assistant conversation could not be read.");
            }
        }

        session = new AssistantConversation(Guid.NewGuid().ToString("N"), [], []);
        store.Write(sessionPath, session);
        return session;
    }

    private async Task<AgentLoop> EnsureLoopAsync(AssistantConversation current, CancellationToken token)
    {
        if (loop is not null) return loop;
        var apiKey = CompatibilityEndpoints.ResolveApiKey(runtimeState, configuration);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AppAssistantGatewayException("CHAT_API_KEY_REQUIRED",
                "Configure a model key before asking the Workbench Assistant.");
        var runtime = services.GetService<McpRuntime>();
        var discovered = runtime is null
            ? new McpToolCatalog([])
            : await McpToolCatalog.BuildAsync(runtime.Host, token).ConfigureAwait(false);
        var specs = discovered.Tools.Select(spec => spec with
        {
            Caller = new ContextualToolCaller(spec.Caller, state, binder, () => turnSelection),
        }).Concat(WorkbenchTools());
        var catalog = new McpToolCatalog(specs);
        var tiers = policy.Tiers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var name in new[]
        {
            "assistant_list_workbenches", "assistant_list_worktrees", "assistant_list_devices",
            "assistant_get_todos", "assistant_get_history",
        }) tiers[name] = SandboxTier.Read;
        var sandbox = new AgentSandbox(new SandboxPolicy(tiers), 20, request =>
        {
            var completion = new TaskCompletionSource<ToolConfirmation>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var id = pending.Add(ConfirmationContextKey, current.SessionId, (decision, _) =>
            {
                completion.TrySetResult(decision);
                return Task.FromResult<object?>(new { status = decision.ToString() });
            });
            runtimeState.Logs.Enqueue(JsonSerializer.Serialize(new
            {
                kind = "confirmation", id, requester = current.SessionId,
                toolName = request.ToolName, arguments = request.ArgumentsSummary,
            }));
            return completion.Task;
        });
        var active = new AgentLoop(
            new DeepSeekClient(apiKey, configuration["DeepSeek:BaseUrl"] ?? "https://api.deepseek.com",
                httpClientFactory.CreateClient("workbench-assistant")),
            catalog,
            RuntimeContext,
            ApiChatService.Settings(configuration, runtimeState),
            sandbox,
            AssistantPrompt);
        active.Apply(ApiChatService.LoopPolicy(configuration, runtimeState));
        active.SessionId = current.SessionId;
        active.RestoreFrom(current.Messages, current.RoundUsages);
        loop = active;
        return active;
    }

    private IEnumerable<AgentToolSpec> WorkbenchTools()
    {
        var caller = new WorkbenchToolCaller(state, gateway);
        static JsonElement Schema(params string[] fields) => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = fields.ToDictionary(name => name, _ => new { type = "string" }, StringComparer.Ordinal),
            required = fields,
        });
        return
        [
            new("assistant_list_workbenches", "List available workbench projects and their IDs.", Schema(), caller, "workbench"),
            new("assistant_list_worktrees", "List worktrees of one workbench project.", Schema("workbenchId"), caller, "workbench"),
            new("assistant_list_devices", "List devices of one worktree.", Schema("workbenchId", "worktreeId"), caller, "workbench"),
            new("assistant_get_todos", "Read open worktree tasks and todos.", Schema("workbenchId", "worktreeId"), caller, "workbench"),
            new("assistant_get_history", "Read recent Git commits of one worktree.", Schema("workbenchId", "worktreeId"), caller, "workbench"),
        ];
    }

    private string RuntimeContext()
    {
        var selected = turnSelection;
        var lines = new List<string>
        {
            $"Available workbenches: {string.Join(", ", state.List().Select(item => $"{item.Name} ({item.WorkbenchId})"))}",
            $"Selected workbench: {selected?.WorkbenchId ?? "none"}",
            $"Selected worktree: {selected?.WorktreeId ?? "none"}",
            $"Selected device: {selected?.DeviceId ?? "none"}",
        };
        if (selected?.WorkbenchId is { } workbenchId)
        {
            var workbench = state.Workbench(workbenchId);
            lines.Add($"Worktrees: {string.Join(", ", workbench.Worktrees.Select(item => $"{item.Name} ({item.WorktreeId})"))}");
            if (selected.WorktreeId is { } worktreeId)
                lines.Add($"Devices: {string.Join(", ", state.ListDevices(workbenchId, worktreeId).Select(item => $"{item.PlcName} ({item.DeviceId})"))}");
        }
        return string.Join('\n', lines);
    }

    private static string AssistantPrompt() => """
        You are the Workbench Assistant in Automation Workbench. Help the user choose a
        workbench project, worktree, and device, and answer questions about available work.
        A selection is optional. Ask a short clarifying question when the intended project,
        worktree, or device is ambiguous. The user can select any of these in the panel;
        never claim a selection happened merely because you suggested it.
        Use assistant_list_workbenches, assistant_list_worktrees, and assistant_list_devices to
        inspect choices. Use assistant_get_todos and assistant_get_history for task and commit
        questions. Ground claims about the user's projects in tool results.
        PLC source, knowledge, TIA, and write tools require a selected device. Version-control
        read tools can use a selected worktree. If scope is missing, ask the user to select it.
        Never invent a device or filesystem path.
        Runtime context arrives as a user message prefixed "Runtime context (updated):";
        treat it as state, not as a question. Answer concisely.
        """;

    private IReadOnlyList<WorkbenchOption> Workbenches() =>
        state.List().Select(item => new WorkbenchOption(item.WorkbenchId, item.Name)).ToArray();

    private async Task<AppAssistantWorkbenchContext?> CurrentContextAsync() =>
        state.Selection?.WorkbenchId is { } id
            ? await gateway.GetContextAsync(id).ConfigureAwait(false)
            : null;

    internal static string Describe(AppAssistantWorkbenchContext? context, IReadOnlyList<WorkbenchOption> workbenches)
    {
        if (context is null)
            return workbenches.Count == 0
                ? "No workbench project is selected or available. Tell me what you want to create or inspect."
                : $"No workbench project is selected. Available projects: {string.Join(", ", workbenches.Select(item => item.Name))}. Which one would you like to work with?";
        var runtime = context.Runtime;
        var focused = runtime.Worktrees.FirstOrDefault(item => item.WorktreeId == runtime.Focus.WorktreeId);
        var lines = new List<string> { $"Workbench '{context.Name}' has {runtime.Worktrees.Count} worktrees." };
        if (focused is not null)
            lines.Add($"Selected worktree '{focused.Name}' has {focused.TodoCount} open todos.");
        else if (runtime.Worktrees.Count > 0)
            lines.Add("Select a worktree here, or ask me to help choose one.");
        if (runtime.Focus.DeviceId is null)
            lines.Add("You can ask about projects, worktrees, tasks, and history now; select a device for PLC-specific work.");
        return string.Join(' ', lines);
    }

    private sealed class WorkbenchToolCaller(WorkbenchApiState state, AppAssistantGateway gateway) : IMcpToolCaller
    {
        public async Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            var input = args is JsonElement value ? value : JsonSerializer.SerializeToElement(args);
            string Required(string name) => input.TryGetProperty(name, out var field)
                && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())
                    ? field.GetString()!
                    : throw new ToolCallException("ASSISTANT_ARGUMENT_REQUIRED", $"{name} is required.", null);
            object result = tool switch
            {
                "assistant_list_workbenches" => state.List().Select(item => new { item.WorkbenchId, item.Name }).ToArray(),
                "assistant_list_worktrees" => state.Workbench(Required("workbenchId")).Worktrees,
                "assistant_list_devices" => state.ListDevices(Required("workbenchId"), Required("worktreeId")),
                "assistant_get_todos" => await gateway.GetTodosAsync(Required("workbenchId"), Required("worktreeId")),
                "assistant_get_history" => await gateway.GetHistoryAsync(Required("workbenchId"), Required("worktreeId"), cancellationToken: cancellationToken),
                _ => throw new ToolCallException("ASSISTANT_TOOL_UNKNOWN", $"Unknown assistant tool '{tool}'.", null),
            };
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result))!;
        }
    }

    private sealed class ContextualToolCaller(
        IMcpToolCaller inner,
        WorkbenchApiState state,
        DeviceToolArgumentBinder binder,
        Func<WorkbenchSelection?> selection) : IMcpToolCaller
    {
        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            var supplied = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args)) ?? new();
            var selected = selection();
            Dictionary<string, object?> bound;
            if (selected?.DeviceId is { } deviceId && selected.WorktreeId is { } worktreeId)
            {
                var device = state.Device(selected.WorkbenchId, worktreeId, deviceId).Context;
                try { bound = binder.Bind(tool, supplied, device); }
                catch (Exception exception) when (exception is ArgumentException or IOException
                    or WorkbenchPathException or WorkbenchLifecycleException)
                {
                    throw new ToolCallException("TOOL_ARGUMENT_BINDING_FAILED", exception.Message,
                        "Use a path under the selected device or correct the tool arguments.");
                }
            }
            else if (selected?.WorktreeId is { } selectedWorktree
                && (tool is "vc_status" or "vc_log" or "vc_diff" or "vc_branches" or "vc_worktrees"))
            {
                var trustedRoot = state.WorktreeRoot(selected.WorkbenchId, selectedWorktree);
                if (supplied.TryGetValue("repoPath", out var given)
                    && given is JsonElement { ValueKind: JsonValueKind.String } path
                    && !string.Equals(path.GetString(), trustedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new ToolCallException("TOOL_ARGUMENT_BINDING_FAILED",
                        "repoPath conflicts with the selected worktree.", null);
                bound = new(supplied, StringComparer.Ordinal) { ["repoPath"] = trustedRoot };
            }
            else
                throw new ToolCallException("DEVICE_SELECTION_REQUIRED",
                    $"'{tool}' needs a selected device. Ask the user to choose one in the assistant panel.", null);
            return inner.CallAsync<T>(tool, bound, cancellationToken);
        }
    }
}
