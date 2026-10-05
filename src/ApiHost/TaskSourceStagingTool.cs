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
        + "Name each object with the sourceObjectId that " + TaskSourceObjectListTool.ToolName
        + " reports — \"<deviceId>:<sourceId>\" — or with a name that id list shows: a bare sourceId, "
        + "the block name, or the manifest-relative path (Blocks/Area/Main [OB1].xml). A knowledge-base "
        + "node id such as block:Main is understood as the name Main. Never invent an id and never build "
        + "one from a knowledge-graph node id. Name takeOverFromTaskId when the object is already staged "
        + "by another active task; that task's stage is released first and the owner is shown on the "
        + "approval card. Staging the same object for this task again is harmless.";

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
            staged.Add(await StageGuardedAsync(device, prepared.TaskId, sourceObjectId, token)
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

    /// <summary>
    /// The stage itself, through the one guarded path that derives the baseline (ADR-0003).
    /// </summary>
    private Task<TaskSourceStage> StageGuardedAsync(
        DeviceContext device, string taskId, string sourceObjectId, CancellationToken token) =>
        TranslateGraphFailure(() => coordinator
            .StageTaskSourceObjectAsync(device.WorkbenchId, device.WorktreeId, taskId, sourceObjectId, token));

    /// <summary>
    /// The graph reports its own constraint codes, and they are not <see cref="ToolCallException"/>s:
    /// without this translation the agent loop collapses them into a code-less <c>AGENT_TOOL_ERROR</c>
    /// and the caller loses both the reason and the way back — the exact dead end a live conversation hit
    /// on "Source object was not registered.".
    /// </summary>
    internal static async Task<TaskSourceStage> TranslateGraphFailure(Func<Task<TaskSourceStage>> stage)
    {
        try
        {
            return await stage().ConfigureAwait(false);
        }
        catch (EngineeringGraphConstraintException ex)
        {
            throw new ToolCallException(ex.Code, ex.Message,
                $"Read the device's source objects with {TaskSourceObjectListTool.ToolName} and pass an id it reports.");
        }
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
    /// Node-kind prefixes of the knowledge base's semantic graph that name the <b>same</b> PLC objects
    /// this tool stages (<c>Mcp.Knowledge.Graph.SemanticPlcGraph.BlockId/DbId/UdtId/TypeId</c>). Their
    /// colon is a kind separator, not a device separator, which is why a value the model copied out of
    /// the knowledge base used to be refused as another device's object. Declared here rather than
    /// referenced: ApiHost does not link the knowledge server, and the vocabulary is four words wide.
    /// </summary>
    private static readonly string[] KnowledgeObjectPrefixes = ["block", "db", "udt", "type"];

    /// <summary>The knowledge base's element prefixes. These never name a storable source object, so a
    /// value carrying one is reported as the element id it is instead of being read as a device name.</summary>
    private static readonly string[] KnowledgeElementPrefixes =
        ["symbol", "io", "udt-member", "db-member", "edge"];

    /// <summary>
    /// Resolves one requested object to its stable <c>{deviceId}:{sourceId}</c> identity, then proves it
    /// exists: a canonical id, a bare id, a name, or a manifest-relative path all resolve against the
    /// device's own manifest, so the agent can act on the names a user actually says. An id that names
    /// another device is refused as a device mismatch before anything is written, and an unresolvable or
    /// ambiguous value lists the candidates instead of guessing.
    /// </summary>
    /// <remarks>
    /// A value that already carries this device's prefix is resolved like a bare one instead of being
    /// trusted: returning it unvalidated used to defer every such failure to the graph's own
    /// "Source object was not registered.", which arrives without this tool's candidate advice.
    /// </remarks>
    private static string CanonicalId(string requested, string deviceId, IReadOnlyList<SourceObjectInfo> manifest)
    {
        var value = StripDevicePrefix(requested.Trim(), deviceId);

        // A rooted path is a caller mistake, not a device-qualified id. Without this the drive letter
        // would be read as the claiming device ("belongs to device 'C'").
        if (Path.IsPathRooted(value))
        {
            throw new ToolCallException("GRAPH_TARGET_NOT_FOUND",
                $"'{requested}' is an absolute path, but source objects are named relative to the device's source root.",
                $"Pass the relative path the manifest lists, the object's name, or the id {TaskSourceObjectListTool.ToolName} reports.");
        }

        var separator = value.IndexOf(':');
        if (separator > 0)
        {
            var head = value[..separator];
            if (KnowledgeObjectPrefixes.Contains(head, StringComparer.OrdinalIgnoreCase))
            {
                // The knowledge base's name half is the manifest's name, so it resolves below.
                value = value[(separator + 1)..];
            }
            else if (KnowledgeElementPrefixes.Contains(head, StringComparer.OrdinalIgnoreCase))
            {
                throw new ToolCallException("GRAPH_TARGET_NOT_FOUND",
                    $"'{requested}' is a knowledge-graph element id ('{head}:'), not a PLC source object id, so there is nothing to stage.",
                    $"Call {TaskSourceObjectListTool.ToolName} to read this device's source object ids, then pass an id it reports.");
            }
            else
            {
                throw new ToolCallException("TASK_SOURCE_DEVICE_MISMATCH",
                    $"Source object '{requested}' belongs to device '{head}', but the active task is bound to device '{deviceId}'.",
                    $"Only source objects of the active task's device can be staged. Call {TaskSourceObjectListTool.ToolName} "
                    + $"for this device's objects: the id it reports starts with '{deviceId}:'.");
            }
        }

        if (value.Length == 0)
        {
            throw new ToolCallException("GRAPH_TARGET_NOT_FOUND",
                $"'{requested}' names no source object.",
                $"Call {TaskSourceObjectListTool.ToolName} for this device's source object ids.");
        }

        var prefix = deviceId + ":";
        var normalized = value.Replace('\\', '/');
        var matches = manifest.Where(item =>
            string.Equals(item.Id, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Name, value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.RelativePath, normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => prefix + matches[0].Id,
            0 => throw new ToolCallException("GRAPH_TARGET_NOT_FOUND",
                $"No source object of device '{deviceId}' matches '{requested}'." + DidYouMean(value, manifest),
                $"Call {TaskSourceObjectListTool.ToolName} to read this device's source object ids, or pass the block name or the manifest-relative path."),
            _ => throw new ToolCallException("GRAPH_TARGET_NOT_FOUND",
                $"'{requested}' matches {matches.Length} source objects of device '{deviceId}': "
                + string.Join(", ", matches.Select(item => item.Id)) + ".",
                "Use the exact source object id."),
        };
    }

    /// <summary>Drops a leading <c>{deviceId}:</c>, so the remainder is resolved exactly like a bare
    /// value. Any other prefix stays in place for the caller to report on.</summary>
    private static string StripDevicePrefix(string requested, string deviceId)
    {
        var prefix = deviceId + ":";
        return requested.StartsWith(prefix, StringComparison.Ordinal) ? requested[prefix.Length..] : requested;
    }

    /// <summary>Bounded "did you mean" for a value that resolved to nothing: the ids whose name, id or
    /// path contains or nearly starts with what was asked for, so a shortened, mistyped or
    /// path-shaped value comes back with the candidate to use.</summary>
    private static string DidYouMean(string value, IReadOnlyList<SourceObjectInfo> manifest)
    {
        var needles = new[] { value, Path.GetFileNameWithoutExtension(value.Replace('\\', '/')) }
            .Where(needle => !string.IsNullOrWhiteSpace(needle))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (needles.Length == 0)
        {
            return string.Empty;
        }

        var close = manifest
            .Where(item => needles.Any(needle => IsClose(item, needle)))
            .Select(item => $"{item.Id} ({item.Name})")
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
        return close.Length == 0 ? string.Empty : " Closest: " + string.Join(", ", close) + ".";
    }

    /// <summary>Containment, or a shared leading run long enough that a mistyped id still names its
    /// object (a `<c>block-mian</c>` finds `<c>block-main</c>`).</summary>
    private static bool IsClose(SourceObjectInfo item, string needle) =>
        item.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || item.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || item.RelativePath.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || SharedPrefix(item.Id, needle) >= 3
        || SharedPrefix(item.Name, needle) >= 3;

    private static int SharedPrefix(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < length
            && char.ToUpperInvariant(left[index]) == char.ToUpperInvariant(right[index]))
        {
            index++;
        }

        return index;
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