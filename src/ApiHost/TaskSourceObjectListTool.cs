using System.Text.Json;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;

/// <summary>
/// The device chat's read-only source-object listing: the ids, names and paths a
/// <see cref="TaskSourceStagingTool"/> call must name, and which active task already stages each one.
///
/// Item 008. Staging used to be the only agent-visible entry point into this id space, so the model had
/// to invent an id — and a live conversation invented a knowledge-graph node id, then the pre-fixed form
/// the device-mismatch error suggested, and stalled on "Source object was not registered.". The listing
/// is the missing read half; it reads the <b>same</b> manifest the staging path resolves against, so the
/// id it reports is exactly the id staging accepts.
///
/// Safety shape:
/// <list type="bullet">
/// <item>read-tier and side-effect free: it opens the graph for one owner read and never registers an
/// entity, stages, releases or invalidates a projection;</item>
/// <item>the device and worktree are the conversation's own validated <see cref="DeviceContext"/>, so
/// the model cannot aim it at another device;</item>
/// <item>it offers the same object set as the task page's picker — instance DBs are dropped, because
/// they are outside the managed-source evidence domain and can never carry a compare baseline, so a row
/// for one could only ever mislead.</item>
/// </list>
/// </summary>
internal sealed class TaskSourceObjectListTool(
    WorkbenchApiState state,
    EngineeringGraphApiFactory graphs,
    ActiveTaskContextService activeTasks)
{
    public const string ToolName = "list_source_objects";
    private const string ServerName = "workbench";

    /// <summary>Page size when the caller names none, and the largest page it can ask for: the model
    /// reads the whole page, so a 774-object export must never arrive in one tool result.</summary>
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public const string Description =
        "List the source objects of this conversation's device, with the sourceObjectId to stage them "
        + "and the active task, if any, that already stages each one. Read-only. Filter with query "
        + "(substring of the name, id, group path or exported path) and category (OB, FB, FC, DB, Tags, "
        + "UDT), and page with offset/limit. Instance DBs are not listed: they are excluded from "
        + "managed-source evidence and can never carry a compare baseline. Use this instead of guessing "
        + "an id, and instead of capture_source_evidence, which needs a live TIA session and returns the "
        + "whole project. The reported activeTask is the task stage_task_source_object would add to.";

    public static JsonElement InputSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string" },
            category = new { type = "string" },
            offset = new { type = "integer" },
            limit = new { type = "integer" },
        },
    });

    /// <summary>The agent-visible spec. <paramref name="device"/> resolves the conversation's device at
    /// call time, so the tool always reads the current context rather than a stale one.</summary>
    public static AgentToolSpec CreateSpec(TaskSourceObjectListTool tool, Func<DeviceContext?> device) =>
        new(ToolName, Description, InputSchema, new Caller(tool, device), ServerName);

    public object List(DeviceContext device, JsonElement input)
    {
        var query = Optional(input, "query");
        var category = Optional(input, "category");
        var offset = Math.Max(0, Integer(input, "offset") ?? 0);
        var limit = Math.Clamp(Integer(input, "limit") ?? DefaultLimit, 1, MaxLimit);

        var workbench = state.Workbench(device.WorkbenchId);
        if (!workbench.Worktrees.Any(item => item.WorktreeId == device.WorktreeId))
        {
            throw new ToolCallException("WORKTREE_NOT_FOUND",
                $"Worktree '{device.WorktreeId}' was not found in workbench '{device.WorkbenchId}'.", null);
        }

        // Read from the manifest, not the projection: this is the same source `CanonicalId` resolves
        // against, so a reported id can never be one staging then refuses.
        var manifest = DeviceSnapshotReader.ReadManifestSourceObjects(device.SourceRoot);
        var comparable = DeviceSnapshotReader.ComparableSourceObjects(manifest);

        // One graph read scope: the owners of every active stage in this worktree, and the active task
        // the staging tool would add to.
        Dictionary<string, WorktreeSourceStage> owners;
        GraphTask? activeTask;
        using (var scope = graphs.Open(workbench))
        {
            owners = scope.Service.ListWorktreeActiveStages(device.WorktreeId)
                .Where(item => !string.IsNullOrWhiteSpace(item.Stage.SourceObjectId))
                .ToDictionary(item => item.Stage.SourceObjectId, StringComparer.Ordinal);
            activeTask = activeTasks.Get(scope.Service, device.WorktreeId);
        }

        var matching = comparable
            .Where(item => Matches(item, device.DeviceId, query, category))
            .ToArray();
        var page = matching.Skip(offset).Take(limit).ToArray();
        var next = offset + page.Length < matching.Length ? offset + page.Length : (int?)null;

        return new
        {
            deviceId = device.DeviceId,
            activeTask = activeTask is null
                ? null
                : new { taskId = activeTask.TaskId, title = activeTask.Title },
            totalCount = comparable.Count,
            excludedNotComparableCount = manifest.Count - comparable.Count,
            matchingCount = matching.Length,
            offset,
            limit,
            returned = page.Length,
            nextOffset = next,
            sourceObjects = page.Select(item =>
            {
                var sourceObjectId = device.DeviceId + ":" + item.Id;
                owners.TryGetValue(sourceObjectId, out var owner);
                return new
                {
                    sourceObjectId,
                    sourceId = item.Id,
                    name = item.Name,
                    category = item.Category,
                    number = item.Number,
                    groupPath = item.GroupPath,
                    relativePath = item.RelativePath,
                    evidenceKind = item.EvidenceKind,
                    stagedByTaskId = owner?.Stage.TaskId,
                    stagedByTaskTitle = owner?.TaskTitle,
                };
            }).ToArray(),
        };
    }

    private static bool Matches(SourceObjectInfo item, string deviceId, string? query, string? category)
    {
        if (!string.IsNullOrWhiteSpace(category)
            && !string.Equals(item.Category, category.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var needle = query.Trim();
        return item.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || item.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (deviceId + ":" + item.Id).Contains(needle, StringComparison.OrdinalIgnoreCase)
            || item.RelativePath.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (item.GroupPath?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string? Optional(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static int? Integer(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    /// <summary>Dispatches the single listing, bound to the conversation's current device.</summary>
    private sealed class Caller(TaskSourceObjectListTool tool, Func<DeviceContext?> device) : IMcpToolCaller
    {
        public Task<T> CallAsync<T>(string name, object args, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(name, ToolName, StringComparison.Ordinal))
                throw new KeyNotFoundException($"Tool '{name}' is not exposed to the agent.");
            var current = device()
                ?? throw new ToolCallException("DEVICE_SELECTION_REQUIRED",
                    $"'{ToolName}' needs a selected device.", "Select a registered device, then call the tool again.");
            var input = args is JsonElement element ? element : JsonSerializer.SerializeToElement(args);
            var result = tool.List(current, input);
            return Task.FromResult(JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result))!);
        }
    }
}
