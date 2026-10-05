using System.Text.Json;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;

/// <summary>
/// The device chat's task-creation tool: while working a device offline, the knowledge agent records a
/// finding it established in the conversation as a worktree task and carries the reasoning with it —
/// background, evidence and proposed solutions land in the task's own description, which the task page
/// renders as Markdown. The capability already existed for the Workbench Assistant
/// (<c>assistant_create_task</c>); this is the device-chat entry point for it.
///
/// Safety shape, in order:
/// <list type="bullet">
/// <item>the task always targets the conversation's own worktree and device. The tool takes no
/// workbench, worktree or device argument, so the model cannot aim a created task at another device or
/// forge an unregistered device id — the device arrives as the chat's validated
/// <see cref="DeviceContext"/>;</item>
/// <item>the call is classified destructive, so <see cref="AgentSandbox"/> suspends the turn on the
/// shared approval card before this class runs at all. The card shows the full arguments, so the user
/// reads the brief that will be recorded before approving it;</item>
/// <item>every field is validated before the write, so an approved call creates exactly the task the
/// user approved.</item>
/// </list>
/// </summary>
internal sealed class TaskCreationTool(
    WorkbenchApiState state,
    EngineeringGraphApiFactory graphs,
    WorktreeTaskStore tasks)
{
    public const string ToolName = "create_task";
    private const string ServerName = "workbench";

    /// <summary>The brief's section headings. The task page renders the description as Markdown, so a
    /// recorded finding reads as labelled sections instead of one undifferentiated paragraph.</summary>
    public const string BackgroundHeading = "## Background";
    public const string EvidenceHeading = "## Evidence";
    public const string ProposedSolutionsHeading = "## Proposed solutions";

    public const string Description =
        "Create a task in this conversation's worktree, bound to this conversation's device, recording a "
        + "finding established in this chat. Requires user approval: the call is refused until the user "
        + "approves it on the approval card. Build the brief from what the conversation actually "
        + "established — background (the situation, the symptom, why it matters), evidence (the blocks, "
        + "networks, tags and tool results that show the problem) and proposedSolutions (the candidate "
        + "fixes you can defend). Never invent evidence: state what a tool result or the source showed, "
        + "and name the block and network ids you used. Omit a section the conversation has nothing for. "
        + "Do not ask the user to restate facts the conversation already established. intent is the goal "
        + "and expectedResult is the completion criterion; both are required.";

    public static JsonElement InputSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            title = new { type = "string" },
            type = new { type = "string" },
            intent = new { type = "string" },
            expectedResult = new { type = "string" },
            background = new { type = "string" },
            evidence = new { type = "string" },
            proposedSolutions = new { type = "string" },
        },
        required = new[] { "title", "type", "intent", "expectedResult" },
    });

    /// <summary>The agent-visible spec. <paramref name="device"/> resolves the conversation's device at
    /// call time, so the tool always acts on the current context rather than a stale one.</summary>
    public static AgentToolSpec CreateSpec(TaskCreationTool tool, Func<DeviceContext?> device) =>
        new(ToolName, Description, InputSchema, new Caller(tool, device), ServerName);

    public object Create(DeviceContext device, JsonElement input)
    {
        var title = Required(input, "title");
        var type = TaskType(Required(input, "type"));
        var intent = Required(input, "intent");
        var expectedResult = Required(input, "expectedResult");
        var brief = ComposeBrief(
            Optional(input, "background"), Optional(input, "evidence"), Optional(input, "proposedSolutions"));

        var workbench = state.Workbench(device.WorkbenchId);
        if (!workbench.Worktrees.Any(item => item.WorktreeId == device.WorktreeId))
            throw new ToolCallException("WORKTREE_NOT_FOUND",
                $"Worktree '{device.WorktreeId}' was not found in workbench '{device.WorkbenchId}'.", null);

        // The legacy task list is imported before the graph write, exactly as the task API and the
        // Workbench Assistant do, so this creation cannot orphan a task that only tasks.json knows.
        tasks.Load(device.WorktreeRoot);
        using var scope = graphs.Open(workbench);
        var task = scope.Service.CreateTask(Guid.NewGuid().ToString("N"), GraphTaskScopeKind.Worktree,
            device.WorktreeId, title, type, GraphTaskStatus.Todo, brief, 0, intent, expectedResult,
            device.DeviceId);

        return new
        {
            taskId = task.TaskId,
            title = task.Title,
            type = task.Type.ToString(),
            status = task.Status.ToString(),
            worktreeId = task.WorktreeId,
            deviceId = task.DeviceId,
            intent = task.Intent,
            expectedResult = task.ExpectedResult,
        };
    }

    /// <summary>The description the task page shows: only the sections the conversation actually
    /// established, in reading order. No section means no description, so the brief never renders empty
    /// headings a reader would read as missing evidence.</summary>
    private static string? ComposeBrief(string? background, string? evidence, string? proposedSolutions)
    {
        var sections = new List<string>();
        AddSection(sections, BackgroundHeading, background);
        AddSection(sections, EvidenceHeading, evidence);
        AddSection(sections, ProposedSolutionsHeading, proposedSolutions);
        return sections.Count == 0 ? null : string.Join("\n\n", sections);
    }

    private static void AddSection(List<string> sections, string heading, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            sections.Add($"{heading}\n\n{text.Trim()}");
    }

    private static GraphTaskType TaskType(string value)
    {
        if (!Enum.TryParse<GraphTaskType>(value.Trim(), true, out var type) || !Enum.IsDefined(type))
            throw new ToolCallException("TASK_TYPE_INVALID",
                $"'{value}' is not a task type. Task type must be Issue, Improvement or Feature.",
                $"Call {ToolName} with type \"Issue\" for a defect found in the code, \"Improvement\" for "
                + "existing behaviour that should change, or \"Feature\" for new behaviour.");
        return type;
    }

    /// <summary>A required field must carry content, not just be present: the loop's schema check only
    /// enforces presence, and a blank goal would persist a task nobody can act on.</summary>
    private static string Required(JsonElement input, string name) =>
        Optional(input, name) ?? throw new ToolCallException("TOOL_ARGUMENT_INVALID",
            $"'{name}' is required and must not be blank.",
            $"Call {ToolName} again with a non-empty {name}.");

    private static string? Optional(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    /// <summary>Dispatches the single creation tool, bound to the conversation's current device.</summary>
    private sealed class Caller(TaskCreationTool tool, Func<DeviceContext?> device) : IMcpToolCaller
    {
        public Task<T> CallAsync<T>(string name, object args, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(name, ToolName, StringComparison.Ordinal))
                throw new KeyNotFoundException($"Tool '{name}' is not exposed to the agent.");
            var current = device()
                ?? throw new ToolCallException("DEVICE_SELECTION_REQUIRED",
                    $"'{ToolName}' needs a selected device.", "Select a registered device, then call the tool again.");
            var input = args is JsonElement element ? element : JsonSerializer.SerializeToElement(args);
            var result = tool.Create(current, input);
            return Task.FromResult(JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result))!);
        }
    }
}
