using System.Text.Json;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;

/// <summary>
/// The agent's task-staging tool (item 005): it proposes adding PLC source objects to the
/// <b>active</b> task's stage and writes only after the user approves the call on the shared
/// <c>AgentSandbox</c> approval card. The tool is registered in-process because the workbench graph
/// and the guarded stage path (<see cref="WorkbenchCoordinator.StageTaskSourceObjectAsync"/>) live in
/// this process; the engineering MCP server cannot reach them.
///
/// Safety shape, in order:
/// <list type="bullet">
/// <item>the active task comes from <see cref="ActiveTaskContextService"/>, exactly as the commit
/// attribution path resolves it — with no active task the tool reports the missing context instead of
/// guessing one;</item>
/// <item>only source objects of that task's device are considered; another device's object is refused
/// before any write;</item>
/// <item>an object owned by another active task is never taken over implicitly: the call must name the
/// owner (it travels in the tool arguments, so the approval card shows it before the write), and a
/// claim that does not match the live owner is refused with the real owner reported;</item>
/// <item>every requested object is validated before the first write, so an approved call stages exactly
/// the objects the user approved. Rejecting the card never reaches this class at all — the sandbox
/// refuses the call and the stage list, the commit gate and the task stay unchanged.</item>
/// </list>
///
/// The write itself reuses <see cref="WorkbenchCoordinator.StageTaskSourceObjectAsync"/>, so the
/// committed-content baseline rule (ADR-0003) stays in exactly one place.
/// </summary>
internal sealed class TaskSourceStagingTool(
    WorkbenchApiState state,
    EngineeringGraphApiFactory graphs,
    ActiveTaskContextService activeTasks,
    WorkbenchCoordinator coordinator)
{
    public const string ToolName = "stage_task_source_object";
    private const string ServerName = "workbench";

    public const string Description =
        "Stage PLC source objects for the active task of the selected worktree, so they become the "
        + "task's TIA compare basis. Requires user approval: the call is refused until the user "
        + "approves it on the approval card. Each object must belong to the active task's device. "
        + "Name takeOverFromTaskId when the object is already staged by another active task; that "
        + "task's stage is released first and the owner is shown on the approval card. Staging the "
        + "same object for this task again is harmless.";

    /// <summary>Object entries, not a bare id list: a take-over must name the owner it releases, and
    /// the approval card shows the tool arguments verbatim.</summary>
    public static JsonElement InputSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            objects = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        sourceObjectId = new { type = "string" },
                        takeOverFromTaskId = new { type = "string" },
                    },
                    required = new[] { "sourceObjectId" },
                },
            },
        },
        required = new[] { "objects" },
    });

    /// <summary>The agent-visible spec. <paramref name="device"/> resolves the conversation's device at
    /// call time, so the tool always acts on the current context rather than a stale one.</summary>
    public static AgentToolSpec CreateSpec(TaskSourceStagingTool tool, Func<DeviceContext?> device) =>
        new(ToolName, Description, InputSchema, new Caller(tool, device), ServerName);

    public async Task<object> StageAsync(DeviceContext device, JsonElement input, CancellationToken token = default)
    {
        var requested = ReadRequestedObjects(input);
        var workbench = state.Workbench(device.WorkbenchId);
        if (!workbench.Worktrees.Any(item => item.WorktreeId == device.WorktreeId))
            throw new ToolCallException("WORKTREE_NOT_FOUND",
                $"Worktree '{device.WorktreeId}' was not found in workbench '{device.WorkbenchId}'.", null);

        // One graph read scope, disposed before the write: the coordinator opens its own store, and
        // the stage route keeps the same read-then-write separation.
        PreparedStage prepared;
        using (var scope = graphs.Open(workbench))
        {
            prepared = Prepare(scope.Service, workbench, device, requested);
        }

        coordinator.RegisterWorkbench(workbench);
        // Take-over releases before the stages, the existing release-then-stage rule. The unique
        // active-owner index still guards the gap: a concurrent stage loses to it, not to a silent
        // second owner.
        if (prepared.Releases.Count > 0)
        {
            using var releaseScope = graphs.Open(workbench);
            foreach (var release in prepared.Releases)
            {
                if (!releaseScope.Service.ReleaseSourceStage(release.TaskId, release.SourceObjectId))
                {
                    throw new ToolCallException("TASK_STAGE_NOT_FOUND",
                        $"Source object '{release.SourceObjectId}' is no longer staged by task '{release.TaskId}'.",
                        "Call the tool again to read the current owner before taking the object over.");
                }
            }
        }

        var staged = new List<TaskSourceStage>();
        foreach (var sourceObjectId in prepared.Targets)
        {
            staged.Add(await coordinator
                .StageTaskSourceObjectAsync(device.WorkbenchId, device.WorktreeId, prepared.TaskId, sourceObjectId, token)
                .ConfigureAwait(false));
        }

        return new
        {
            taskId = prepared.TaskId,
            taskTitle = prepared.TaskTitle,
            staged = staged.Select(stage => new
            {
                stage.SourceObjectId,
                stage.DeviceId,
                stage.StagedUtc,
                stage.BaselineEvidenceJson,
            }).ToArray(),
            takenOverFrom = prepared.Releases.Select(release => new
            {
                release.SourceObjectId,
                release.TaskId,
                release.TaskTitle,
            }).ToArray(),
        };
    }

    /// <summary>Everything the call will do, computed before the first write: the active task, the
    /// canonical id of every requested object, and which stages must be released first.</summary>
    private PreparedStage Prepare(
        EngineeringGraphService graph,
        WorkbenchMetadata workbench,
        DeviceContext device,
        IReadOnlyList<RequestedObject> requested)
    {
        var task = activeTasks.Get(graph, device.WorktreeId)
            ?? throw new ToolCallException("ACTIVE_TASK_REQUIRED",
                "This worktree has no active task, so there is no task stage to add source objects to.",
                "Open the task on the task page to make it active, then call this tool again.");
        if (task.ScopeKind != GraphTaskScopeKind.Worktree || task.TargetKind != GraphTaskTargetKind.Device
            || string.IsNullOrWhiteSpace(task.DeviceId))
        {
            throw new ToolCallException("TASK_DEVICE_REQUIRED",
                $"Task '{task.Title}' is not a device-bound worktree task, so it cannot stage source objects.",
                "Select a device-bound worktree task as the active task.");
        }

        // The conversation's device and the task's device must agree: otherwise the caller would be
        // approving objects from one device for another device's task.
        if (!string.Equals(task.DeviceId, device.DeviceId, StringComparison.Ordinal))
        {
            throw new ToolCallException("TASK_DEVICE_MISMATCH",
                $"The active task is bound to device '{task.DeviceId}', but this conversation is on device '{device.DeviceId}'.",
                "Select the device the active task is bound to, then call this tool again.");
        }

        var deviceId = task.DeviceId;
        var worktreeId = task.WorktreeId!;
        var manifest = DeviceSnapshotReader.ReadManifestSourceObjects(device.SourceRoot);
        graph.RegisterEntities(manifest.Select(source => new GraphEntity(GraphEntityKind.SourceObject,
            deviceId + ":" + source.Id, workbench.WorkbenchId, worktreeId, deviceId, source.RelativePath)));

        var owners = graph.ListWorktreeActiveStages(worktreeId)
            .Where(item => item.Stage.TaskId != task.TaskId)
            .ToDictionary(item => item.Stage.SourceObjectId, StringComparer.Ordinal);

        var targets = new List<string>();
        var releases = new List<StageRelease>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in requested)
        {
            var sourceObjectId = CanonicalId(item.SourceObjectId, deviceId, manifest);
            if (!seen.Add(sourceObjectId))
            {
                // The graph stage list is keyed by (task, object): staging the same object twice in
                // one call is one change, not two.
                continue;
            }

            if (owners.TryGetValue(sourceObjectId, out var owner))
            {
                var declared = item.TakeOverFromTaskId;
                if (string.IsNullOrWhiteSpace(declared)
                    || !Matches(declared, owner.Stage.TaskId, owner.TaskTitle))
                {
                    throw new ToolCallException("SOURCE_ALREADY_STAGED",
                        $"Source object '{sourceObjectId}' is staged by task '{owner.TaskTitle}' ({owner.Stage.TaskId}). "
                        + (string.IsNullOrWhiteSpace(declared)
                            ? "This call does not name that task."
                            : $"This call names '{declared}' instead."),
                        $"Call this tool again with takeOverFromTaskId '{owner.Stage.TaskId}' for "
                        + $"'{sourceObjectId}'. The approval card will then show whose stage is released first.");
                }

                releases.Add(new StageRelease(owner.Stage.TaskId, owner.TaskTitle, sourceObjectId));
            }

            targets.Add(sourceObjectId);
        }

        return new PreparedStage(task.TaskId, task.Title, targets, releases);
    }

    /// <summary>
    /// Resolves one requested object to its stable <c>{deviceId}:{sourceId}</c> identity. A canonical
    /// id is kept as it is; a bare id, name, or relative path is resolved against the device's own
    /// manifest, so the agent can act on the names a user actually says. An id that names another
    /// device is refused as a device mismatch before anything is written, and an unresolvable or
    /// ambiguous value lists the candidates instead of guessing.
    /// </summary>
    private static string CanonicalId(string requested, string deviceId, IReadOnlyList<SourceObjectInfo> manifest)
    {
        var prefix = deviceId + ":";
        if (requested.StartsWith(prefix, StringComparison.Ordinal))
        {
            if (requested.Length == prefix.Length)
            {
                throw new ToolCallException("GRAPH_TARGET_NOT_FOUND",
                    $"'{requested}' names no source object.", "Provide a source object id, name, or relative path.");
            }
            return requested;
        }

        var separator = requested.IndexOf(':');
        if (separator > 0)
        {
            var claimedDevice = requested[..separator];
            throw new ToolCallException("TASK_SOURCE_DEVICE_MISMATCH",
                $"Source object '{requested}' belongs to device '{claimedDevice}', but the active task is bound to device '{deviceId}'.",
                $"Only source objects of the active task's device can be staged. Use an object id starting with '{prefix}'.");
        }

        var normalized = requested.Replace('\\', '/');
        var matches = manifest.Where(item =>
            string.Equals(item.Id, requested, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Name, requested, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.RelativePath, normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => prefix + matches[0].Id,
            0 => throw new ToolCallException("GRAPH_TARGET_NOT_FOUND",
                $"No source object of device '{deviceId}' matches '{requested}'.",
                "Use the source object id or the block name the device's registered objects report."),
            _ => throw new ToolCallException("GRAPH_TARGET_NOT_FOUND",
                $"'{requested}' matches {matches.Length} source objects of device '{deviceId}': "
                + string.Join(", ", matches.Select(item => item.Id)) + ".",
                "Use the exact source object id."),
        };
    }

    private static bool Matches(string declared, string taskId, string taskTitle) =>
        string.Equals(declared.Trim(), taskId, StringComparison.Ordinal)
        || string.Equals(declared.Trim(), taskTitle, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<RequestedObject> ReadRequestedObjects(JsonElement input)
    {
        if (!input.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Array
            || objects.GetArrayLength() == 0)
        {
            throw new ToolCallException("TOOL_ARGUMENT_INVALID",
                "Provide at least one source object to stage.", "Pass objects: [{ sourceObjectId }].");
        }

        var requested = new List<RequestedObject>();
        foreach (var element in objects.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("sourceObjectId", out var idElement)
                || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(idElement.GetString()))
            {
                throw new ToolCallException("TOOL_ARGUMENT_INVALID",
                    "Every entry needs a non-empty sourceObjectId.", "Pass objects: [{ sourceObjectId }].");
            }

            var owner = element.TryGetProperty("takeOverFromTaskId", out var ownerElement)
                && ownerElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(ownerElement.GetString())
                    ? ownerElement.GetString()
                    : null;
            requested.Add(new RequestedObject(idElement.GetString()!.Trim(), owner));
        }

        return requested;
    }

    private sealed record RequestedObject(string SourceObjectId, string? TakeOverFromTaskId);
    private sealed record StageRelease(string TaskId, string TaskTitle, string SourceObjectId);
    private sealed record PreparedStage(
        string TaskId, string TaskTitle, IReadOnlyList<string> Targets, IReadOnlyList<StageRelease> Releases);

    /// <summary>Dispatches the single staging tool, bound to the conversation's current device.</summary>
    private sealed class Caller(TaskSourceStagingTool tool, Func<DeviceContext?> device) : IMcpToolCaller
    {
        public async Task<T> CallAsync<T>(string name, object args, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(name, ToolName, StringComparison.Ordinal))
                throw new KeyNotFoundException($"Tool '{name}' is not exposed to the agent.");
            var current = device()
                ?? throw new ToolCallException("DEVICE_SELECTION_REQUIRED",
                    $"'{ToolName}' needs a selected device.", "Select a registered device, then call the tool again.");
            var input = args is JsonElement element ? element : JsonSerializer.SerializeToElement(args);
            var result = await tool.StageAsync(current, input, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result))!;
        }
    }
}