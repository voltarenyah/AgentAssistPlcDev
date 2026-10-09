using System.Text.Json;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
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
    EngineeringGraphApiFactory graphs,
    WorkbenchCoordinator coordinator,
    WorktreeTaskStore tasks,
    ApiMcpGateway mcpGateway,
    PendingToolActions pending,
    SandboxPolicy policy)
{
    internal const string ConfirmationContextKey = "workbench-assistant";

    public sealed record WorkbenchOption(string WorkbenchId, string Name);
    public sealed record ChoiceOption(string Value, string Label, string? Description);
    public sealed record ChoiceDecision(string Question, IReadOnlyList<ChoiceOption> Options);
    public sealed record ManagedChange(string Kind, string WorkbenchId, string? WorktreeId, string? TaskId, string? DeviceId = null);
    public sealed record Turn(
        AppAssistantWorkbenchContext? Context,
        IReadOnlyList<WorkbenchOption> Workbenches,
        string Answer,
        string SessionId,
        ChoiceDecision? Decision = null,
        ManagedChange? Change = null);

    private readonly SemaphoreSlim turnGate = new(1, 1);
    private readonly string sessionPath = environment.IsEnvironment("Testing")
        ? Path.Combine(Path.GetTempPath(), "workbench-assistant-tests", Guid.NewGuid().ToString("N"), "session.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutomationWorkbench", "assistant", "session.json");
    private AssistantConversation? session;
    private AgentLoop? loop;
    private WorkbenchSelection? turnSelection;
    private ChoiceDecision? turnDecision;
    private ManagedChange? turnChange;

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
            turnDecision = null;
            turnChange = null;
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
            return new Turn(context, Workbenches(), answer, current.SessionId, turnDecision, turnChange);
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
        // Raw MCP operations bypass the managed workbench, worktree, and source-commit lifecycles.
        var specs = discovered.Tools
            .Where(spec => spec.Name is not ("create_project" or "vc_add_worktree" or "vc_commit"))
            .Select(spec => spec with
        {
            Caller = new ContextualToolCaller(spec.Caller, state, binder, () => turnSelection),
        }).Concat(WorkbenchTools());
        var catalog = new McpToolCatalog(specs);
        var tiers = policy.Tiers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var name in new[]
        {
            "assistant_list_workbenches", "assistant_list_worktrees", "assistant_list_devices",
            "assistant_list_tasks", "assistant_get_history", "assistant_list_tia_sessions",
            "assistant_list_branch_start_points", "assistant_present_choices",
        }) tiers[name] = SandboxTier.Read;
        tiers["assistant_select_scope"] = SandboxTier.Write;
        tiers["assistant_open_tia_project"] = SandboxTier.Write;
        foreach (var name in new[] { "assistant_create_workbench", "assistant_create_worktree", "assistant_create_task" })
            tiers[name] = SandboxTier.Destructive;
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
        var caller = new WorkbenchToolCaller(state, gateway, graphs, coordinator, tasks, mcpGateway,
            decision => turnDecision = decision, change =>
            {
                turnDecision = null;
                turnChange = change;
                if (change.Kind == "selection") turnSelection = state.Selection;
            });
        static JsonElement Schema(params string[] fields) => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = fields.ToDictionary(name => name, _ => new { type = "string" }, StringComparer.Ordinal),
            required = fields,
        });
        static JsonElement Fields(string[] required, params string[] optional) => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = required.Concat(optional).ToDictionary(name => name, _ => new { type = "string" }, StringComparer.Ordinal),
            required,
        });
        // The allowed task types travel in the schema: the agent loop validates every call against it
        // before dispatch, so a synonym is refused as a correctable argument error instead of parking
        // the turn on an approval card and failing after the user has approved it (the device chat's
        // sibling tool failed exactly that way with a live "bug").
        static JsonElement TaskCreationSchema() => JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                workbenchId = new { type = "string" },
                worktreeId = new { type = "string" },
                deviceId = new { type = "string" },
                title = new { type = "string" },
                type = new { type = "string", @enum = new[] { "Issue", "Improvement", "Feature" } },
                intent = new { type = "string" },
                expectedResult = new { type = "string" },
                description = new { type = "string" },
            },
            required = new[] { "workbenchId", "worktreeId", "deviceId", "title", "type", "intent", "expectedResult" },
        });
        return
        [
            new("assistant_list_workbenches", "List available workbench projects and their IDs.", Schema(), caller, "workbench"),
            new("assistant_list_worktrees", "List worktrees of one workbench project.", Schema("workbenchId"), caller, "workbench"),
            new("assistant_list_devices", "List devices of one worktree.", Schema("workbenchId", "worktreeId"), caller, "workbench"),
            new("assistant_list_tasks", "List project and worktree tasks, including bound devices, before proposing another task.", Schema("workbenchId", "worktreeId"), caller, "workbench"),
            new("assistant_get_history", "Read recent Git commits of one worktree.", Schema("workbenchId", "worktreeId"), caller, "workbench"),
            new("assistant_list_tia_sessions", "List open TIA engineering sessions for managed workbench creation.", Schema(), caller, "workbench"),
            new("assistant_list_branch_start_points", "List eligible and ineligible native savepoints or Git start points for a managed linked worktree.", Schema("workbenchId"), caller, "workbench"),
            new("assistant_select_scope", "Select an existing workbench, optionally one of its worktrees and one registered device, so subsequent tools have that context.", Fields(["workbenchId"], "worktreeId", "deviceId"), caller, "workbench"),
            new("assistant_present_choices", "Show the user selectable answer options for one necessary question. Use option values from real tool results where applicable.", JsonSerializer.SerializeToElement(new { type = "object", properties = new { question = new { type = "string" }, options = new { type = "array", items = new { type = "object", properties = new { value = new { type = "string" }, label = new { type = "string" }, description = new { type = "string" } }, required = new[] { "value", "label" } } } }, required = new[] { "question", "options" } }), caller, "workbench"),
            new("assistant_open_tia_project", "Open one worktree's registered TIA project in TIA Portal with its user interface, so live TIA work can continue. Attaches to a running TIA Portal that already shows the project instead of starting a second one. Opening TIA can take a minute or two.", Schema("workbenchId", "worktreeId"), caller, "workbench"),
            new("assistant_create_workbench", "Create a managed workbench with its initial master worktree from exactly one open TIA session or existing .ap17 file. Requires user approval.", Fields(["name"], "rootPath", "engineeringSessionId", "engineeringProjectPath"), caller, "workbench"),
            new("assistant_create_worktree", "Create a managed linked worktree from an eligible native savepoint or Git start point. Requires user approval.", Fields(["workbenchId", "name", "branch"], "startPoint", "sourceWorktreeId", "sourceGitSha"), caller, "workbench"),
            new("assistant_create_task", "Create a device-bound worktree task with a goal and expected result. Requires user approval. type must be exactly Issue (a defect in the code), Improvement, or Feature.", TaskCreationSchema(), caller, "workbench"),
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
        You are the Workbench Assistant in Automation Workbench. Complete requested work using
        your tools. Never send the user to a UI dialog, button, or menu for an operation you can
        perform. Ask only for information needed to proceed, and show selectable options with
        assistant_present_choices when the choice comes from known projects, sessions, devices,
        task types, or eligible savepoints. After the user chooses, continue the operation rather
        than giving instructions. Present one decision at a time. Use names in replies and IDs
        only to disambiguate. Do not invent paths, sessions, devices, or savepoints.

        For a managed workbench, obtain a name and exactly one source: an open TIA session ID
        from assistant_list_tia_sessions or an existing .ap17 file path supplied by the user.
        The root path is optional. assistant_create_workbench creates the initial master worktree
        and imports devices. Do not create a second worktree merely to start device work.
        For a linked worktree, identify the workbench, call assistant_list_branch_start_points,
        and ask the user to choose among selectable baselines if needed. Pass sourceWorktreeId
        and sourceGitSha for a native savepoint, or startPoint for a Git-only baseline. Use
        assistant_create_worktree. Never use raw vc_add_worktree or create_project for this.
        When exactly one eligible baseline exists, use it without asking the user to choose it.
        For a worktree task, identify its workbench, worktree, and registered device. If only
        one worktree or device exists, use it without asking. Obtain title, type (Issue,
        Improvement, or Feature), goal (intent), and expected result; infer type when obvious.
        Use assistant_list_tasks if an existing task may already cover the request, then use
        assistant_create_task. Do not create a duplicate or ask for optional descriptions.

        Use assistant_open_tia_project to open a worktree's registered project in TIA Portal when
        live TIA work is needed; it reuses a running TIA Portal that already shows the project, so
        do not tell the user to open it by hand.

        Creation tools require approval. The user sees the approval card before the tool runs;
        do not ask for an additional approval in prose. After approval, report the actual result
        and stop. If a tool fails, explain the specific failure and ask only for a corrective
        choice. Use list tools only when runtime context or prior tool results do not already
        answer the question. Do not propose extra work after completing the requested action.
        If approval is rejected, state that nothing was created and stop; do not immediately
        offer to resubmit the same proposal.
        If the user asks to focus an existing project, worktree, or device, use
        assistant_select_scope after resolving IDs; do not ask them to click a selection control.
        PLC source and knowledge tools require a selected device. Do not use raw vc_commit for
        source changes; source commits use the guarded managed transaction.
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
                : $"No workbench project is selected. Available projects: {string.Join(", ", workbenches.Select(item => item.Name))}. Tell me which one to focus, or ask me to create one.";
        var runtime = context.Runtime;
        var focused = runtime.Worktrees.FirstOrDefault(item => item.WorktreeId == runtime.Focus.WorktreeId);
        var lines = new List<string> { $"Workbench '{context.Name}' has {runtime.Worktrees.Count} worktrees." };
        if (focused is not null)
            lines.Add($"Selected worktree: '{focused.Name}'. Ask me to check its tasks before creating another.");
        else if (runtime.Worktrees.Count > 0)
            lines.Add("Ask me to focus a worktree or help choose one.");
        if (runtime.Focus.DeviceId is null)
            lines.Add("You can ask about projects, worktrees, tasks, and history now; ask me to focus a device for PLC-specific work.");
        return string.Join(' ', lines);
    }

    private sealed class WorkbenchToolCaller(
        WorkbenchApiState state, AppAssistantGateway gateway, EngineeringGraphApiFactory graphs,
        WorkbenchCoordinator coordinator, WorktreeTaskStore tasks, ApiMcpGateway mcpGateway,
        Action<ChoiceDecision> setDecision, Action<ManagedChange> setChange) : IMcpToolCaller
    {
        public async Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            var input = args is JsonElement value ? value : JsonSerializer.SerializeToElement(args);
            string Required(string name) => input.TryGetProperty(name, out var field)
                && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())
                    ? field.GetString()!
                    : throw new ToolCallException("ASSISTANT_ARGUMENT_REQUIRED", $"{name} is required.", null);
            string? Optional(string name) => input.TryGetProperty(name, out var field)
                && field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString())
                    ? field.GetString() : null;
            object result = tool switch
            {
                "assistant_list_workbenches" => state.List().Select(item => new { item.WorkbenchId, item.Name }).ToArray(),
                "assistant_list_worktrees" => state.Workbench(Required("workbenchId")).Worktrees,
                "assistant_list_devices" => state.ListDevices(Required("workbenchId"), Required("worktreeId")),
                "assistant_list_tasks" => ListTasks(Required("workbenchId"), Required("worktreeId")),
                "assistant_get_history" => await gateway.GetHistoryAsync(Required("workbenchId"), Required("worktreeId"), cancellationToken: cancellationToken),
                "assistant_list_tia_sessions" => await mcpGateway.For("list_sessions").CallAsync<JsonElement>("list_sessions", new { }, cancellationToken),
                "assistant_list_branch_start_points" => await ListStartPoints(Required("workbenchId"), cancellationToken),
                "assistant_select_scope" => SelectScope(Required("workbenchId"), Optional("worktreeId"), Optional("deviceId")),
                "assistant_present_choices" => PresentChoices(input),
                "assistant_open_tia_project" => await OpenTiaProject(Required("workbenchId"), Required("worktreeId"), cancellationToken),
                "assistant_create_workbench" => await CreateWorkbench(Required("name"), Optional("rootPath"), Optional("engineeringSessionId"), Optional("engineeringProjectPath"), cancellationToken),
                "assistant_create_worktree" => await CreateWorktree(Required("workbenchId"), Required("name"), Required("branch"), Optional("startPoint"), Optional("sourceWorktreeId"), Optional("sourceGitSha"), cancellationToken),
                "assistant_create_task" => CreateTask(Required("workbenchId"), Required("worktreeId"), Required("deviceId"), Required("title"), Required("type"), Required("intent"), Required("expectedResult"), Optional("description")),
                _ => throw new ToolCallException("ASSISTANT_TOOL_UNKNOWN", $"Unknown assistant tool '{tool}'.", null),
            };
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result))!;
        }

        private object SelectScope(string workbenchId, string? worktreeId, string? deviceId)
        {
            state.Workbench(workbenchId);
            if (deviceId is not null && worktreeId is null)
                throw new ToolCallException("WORKTREE_SELECTION_REQUIRED", "Choose the worktree for this device.", null);
            if (worktreeId is not null) state.Worktree(workbenchId, worktreeId);
            if (deviceId is not null) state.Device(workbenchId, worktreeId!, deviceId);
            state.Select(workbenchId, worktreeId, deviceId);
            setChange(new ManagedChange("selection", workbenchId, worktreeId, null, deviceId));
            return new { selected = true, workbenchId, worktreeId, deviceId };
        }

        private object PresentChoices(JsonElement input)
        {
            var question = input.GetProperty("question").GetString();
            var options = input.GetProperty("options").Deserialize<ChoiceOption[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (string.IsNullOrWhiteSpace(question) || options is not { Length: > 0 and <= 8 }
                || options.Any(option => string.IsNullOrWhiteSpace(option.Value) || string.IsNullOrWhiteSpace(option.Label))
                || options.Select(option => option.Value).Distinct(StringComparer.Ordinal).Count() != options.Length)
                throw new ToolCallException("ASSISTANT_CHOICES_INVALID", "Provide one question and 1 to 8 distinct choices.", null);
            setDecision(new ChoiceDecision(question, options));
            return new { shown = true, question, options };
        }

        private async Task<object> CreateWorkbench(string name, string? rootPath, string? sessionId, string? projectPath, CancellationToken ct)
        {
            if ((sessionId is null) == (projectPath is null) ||
                (sessionId is not null && !int.TryParse(sessionId, out _)))
                throw new ToolCallException("ASSISTANT_SOURCE_REQUIRED", "Choose one open TIA session ID or an existing .ap17 project file.", null);
            var result = await coordinator.CreateWorkbenchAsync(
                new CreateWorkbenchRequest(name, rootPath, sessionId is null ? null : int.Parse(sessionId), projectPath), ct);
            state.Add(result.Workbench);
            setChange(new ManagedChange("workbench", result.Workbench.WorkbenchId, result.Worktree.WorktreeId, null));
            return new { result.Workbench.WorkbenchId, result.Workbench.Name, result.Worktree.WorktreeId,
                initialWorktree = result.Worktree.Name, devices = result.Devices.Select(device => new { device.DeviceId, device.PlcName }) };
        }

        private async Task<IReadOnlyList<BranchStartPoint>> ListStartPoints(string workbenchId, CancellationToken ct)
        {
            coordinator.RegisterWorkbench(state.Workbench(workbenchId));
            return await coordinator.ListBranchStartPointsAsync(workbenchId, ct);
        }

        private async Task<object> OpenTiaProject(string workbenchId, string worktreeId, CancellationToken ct)
        {
            var worktree = state.Worktree(workbenchId, worktreeId);
            var result = await coordinator.ShowWorktreeProjectInTiaAsync(worktree, ct).ConfigureAwait(false);
            return new
            {
                opened = true,
                projectName = result.ProjectName,
                projectPath = result.ProjectPath,
                reusedRunningSession = result.ReusedRunningSession,
                withUI = result.WithUI,
            };
        }

        private async Task<object> CreateWorktree(string workbenchId, string name, string branch,
            string? startPoint, string? sourceWorktreeId, string? sourceGitSha, CancellationToken ct)
        {
            if ((sourceWorktreeId is null) != (sourceGitSha is null))
                throw new ToolCallException("ASSISTANT_BASE_REQUIRED", "Choose a complete source savepoint.", null);
            var source = sourceWorktreeId is null ? null : new SourceSavepointSelection(sourceWorktreeId, sourceGitSha!);
            var result = await coordinator.CreateWorktreeAsync(
                new CreateWorktreeRequest(state.Workbench(workbenchId), name, branch, startPoint, source), ct);
            state.Refresh(workbenchId);
            setChange(new ManagedChange("worktree", workbenchId, result.WorktreeId, null));
            return new { result.WorktreeId, result.Name, result.Branch };
        }

        private object CreateTask(string workbenchId, string worktreeId, string deviceId, string title,
            string type, string intent, string expectedResult, string? description)
        {
            var worktree = state.Worktree(workbenchId, worktreeId);
            if (!worktree.DeviceIds.Contains(deviceId, StringComparer.Ordinal))
                throw new ToolCallException("TASK_DEVICE_REQUIRED", "Choose a registered device in this worktree.", null);
            if (!Enum.TryParse<GraphTaskType>(type, true, out var taskType) || !Enum.IsDefined(taskType))
                throw new ToolCallException("ASSISTANT_TASK_TYPE_INVALID", "Task type must be Issue, Improvement, or Feature.", null);
            tasks.Load(state.WorktreeRoot(workbenchId, worktreeId));
            using var scope = graphs.Open(state.Workbench(workbenchId));
            var task = scope.Service.CreateTask(Guid.NewGuid().ToString("N"), GraphTaskScopeKind.Worktree,
                worktreeId, title, taskType, description: description, intent: intent,
                expectedResult: expectedResult, deviceId: deviceId);
            setChange(new ManagedChange("task", workbenchId, worktreeId, task.TaskId));
            return new { task.TaskId, task.Title, task.Type, task.DeviceId, task.Intent, task.ExpectedResult };
        }

        private object ListTasks(string workbenchId, string worktreeId)
        {
            state.Worktree(workbenchId, worktreeId);
            tasks.Load(state.WorktreeRoot(workbenchId, worktreeId));
            using var scope = graphs.Open(state.Workbench(workbenchId));
            return scope.Service.ListTasks()
                .Where(task => task.ScopeKind == GraphTaskScopeKind.Project || task.WorktreeId == worktreeId)
                .Select(task => new
                {
                    task.TaskId, task.Title, task.ScopeKind, task.WorktreeId, task.DeviceId,
                    task.Type, task.Status, task.Intent, task.ExpectedResult,
                }).ToArray();
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
                    $"'{tool}' needs a selected device. Ask which registered device to use, then select it with assistant_select_scope.", null);
            return inner.CallAsync<T>(tool, bound, cancellationToken);
        }
    }
}
