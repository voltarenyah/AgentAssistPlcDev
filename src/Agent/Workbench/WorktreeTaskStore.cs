using System.Text.Json;
using Agent.Workbench.EngineeringGraph;

namespace Agent.Workbench;

/// <summary>
/// Persistence for the worktree-scoped task list (worktrees/&lt;name&gt;/tasks.json).
/// Tasks live inside the worktree directory so they travel with the branch and are
/// deleted with the worktree (buildnote/plan/project-worktree-landing-pages.md D1).
/// </summary>
public sealed class WorktreeTaskStore
{
    public const int CurrentVersion = 1;

    private readonly AtomicJsonStore _store;
    public IReadOnlyList<LegacyImportDiagnostic> LastImportDiagnostics { get; private set; } = [];

    public WorktreeTaskStore(AtomicJsonStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public static string TasksPath(string worktreeRoot) =>
        Path.Combine(worktreeRoot, "tasks.json");

    /// <summary>Missing or unreadable tasks.json yields an empty list — a worktree without
    /// tasks is the normal state, and a corrupt file must not break the overview.</summary>
    public WorktreeTaskList Load(string worktreeRoot)
    {
        if (TryOpenGraph(worktreeRoot, out var graph, out var graphStore, out var worktreeId, out var workbench))
        {
            using (graphStore)
            {
                ImportRegisteredWorktrees(graph, workbench!);
                return new WorktreeTaskList(CurrentVersion, graph.ListTasks(worktreeId).Select(item => ToLegacy(item)).ToList());
            }
        }
        var path = TasksPath(worktreeRoot);
        if (!File.Exists(path))
        {
            return new WorktreeTaskList(CurrentVersion, []);
        }

        try
        {
            return _store.Read<WorktreeTaskList>(path);
        }
        catch (JsonException)
        {
            return new WorktreeTaskList(CurrentVersion, []);
        }
    }

    public WorktreeTask Add(
        string worktreeRoot,
        string title,
        string? details = null,
        IReadOnlyList<string>? elementRefs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (TryOpenGraph(worktreeRoot, out var graph, out var graphStore, out var worktreeId, out var workbench))
        {
            using (graphStore)
            {
                ImportRegisteredWorktrees(graph, workbench!);
                var graphTask = graph.CreateTask(Guid.NewGuid().ToString("N"), GraphTaskScopeKind.Worktree, worktreeId, title, GraphTaskType.Feature, description: details, intent: "Legacy task", expectedResult: "Unspecified");
                var legacy = new WorktreeTask(graphTask.TaskId, graphTask.Title, graphTask.Description, WorktreeTaskStatus.Todo, elementRefs?.ToArray() ?? [], graphTask.CreatedUtc!.Value, null);
                graph.UpdateTask(graphTask.TaskId, current => ToGraph(legacy, current));
                SaveLegacy(worktreeRoot, LoadLegacy(worktreeRoot) with { Tasks = [..LoadLegacy(worktreeRoot).Tasks, legacy] });
                return legacy;
            }
        }
        var list = LoadLegacy(worktreeRoot);
        var task = new WorktreeTask(
            Guid.NewGuid().ToString("N"),
            title,
            details,
            WorktreeTaskStatus.Todo,
            elementRefs?.ToArray() ?? [],
            DateTimeOffset.UtcNow,
            null);
        list.Tasks.Add(task);
        Save(worktreeRoot, list);
        return task;
    }

    /// <summary>Applies a mutation to one task and persists. Owns the DoneUtc lifecycle:
    /// set when the status transitions to Done, cleared when it leaves Done.</summary>
    public WorktreeTask? Update(
        string worktreeRoot,
        string taskId,
        Func<WorktreeTask, WorktreeTask> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (TryOpenGraph(worktreeRoot, out var graph, out var graphStore, out var worktreeId, out var workbench))
        {
            using (graphStore)
            {
                ImportRegisteredWorktrees(graph, workbench!);
                var current = graph.ListTasks(worktreeId).FirstOrDefault(t => t.TaskId == taskId);
                if (current is null) return null;
                var result = change(ToLegacy(current));
                if (result.Status == WorktreeTaskStatus.Done && current.Status != GraphTaskStatus.Done)
                    result = result with { DoneUtc = DateTimeOffset.UtcNow };
                else if (result.Status != WorktreeTaskStatus.Done)
                    result = result with { DoneUtc = null };
                var updatedGraph = graph.UpdateTask(taskId, _ => ToGraph(result, current));
                if (updatedGraph is not null)
                {
                    var legacyList = LoadLegacy(worktreeRoot);
                    var legacyIndex = legacyList.Tasks.FindIndex(item => item.TaskId == taskId);
                    if (legacyIndex >= 0) { legacyList.Tasks[legacyIndex] = result; SaveLegacy(worktreeRoot, legacyList); }
                }
                return updatedGraph is null ? null : ToLegacy(updatedGraph, result.ElementRefs);
            }
        }
        var list = LoadLegacy(worktreeRoot);
        var index = list.Tasks.FindIndex(task =>
            string.Equals(task.TaskId, taskId, StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        var previous = list.Tasks[index];
        var updated = change(previous);
        if (updated.Status == WorktreeTaskStatus.Done
            && previous.Status != WorktreeTaskStatus.Done)
        {
            updated = updated with { DoneUtc = DateTimeOffset.UtcNow };
        }
        else if (updated.Status != WorktreeTaskStatus.Done)
        {
            updated = updated with { DoneUtc = null };
        }

        list.Tasks[index] = updated;
        Save(worktreeRoot, list);
        return updated;
    }

    public bool Delete(string worktreeRoot, string taskId)
    {
        if (TryOpenGraph(worktreeRoot, out var graph, out var graphStore, out var worktreeId, out var workbench))
        {
            using (graphStore)
            {
                ImportRegisteredWorktrees(graph, workbench!);
                var graphRemoved = graph.DeleteTask(taskId);
                if (graphRemoved)
                {
                    var legacyList = LoadLegacy(worktreeRoot);
                    if (legacyList.Tasks.RemoveAll(item => item.TaskId == taskId) > 0) SaveLegacy(worktreeRoot, legacyList);
                }
                return graphRemoved;
            }
        }
        var list = LoadLegacy(worktreeRoot);
        var removed = list.Tasks.RemoveAll(task =>
            string.Equals(task.TaskId, taskId, StringComparison.Ordinal));
        if (removed == 0)
        {
            return false;
        }

        Save(worktreeRoot, list);
        return true;
    }

    private void Save(string worktreeRoot, WorktreeTaskList list) =>
        _store.Write(TasksPath(worktreeRoot), list with { Version = CurrentVersion });
    private void SaveLegacy(string worktreeRoot, WorktreeTaskList list) => Save(worktreeRoot, list);

    private WorktreeTaskList LoadLegacy(string worktreeRoot)
    {
        var path = TasksPath(worktreeRoot);
        if (!File.Exists(path)) return new WorktreeTaskList(CurrentVersion, []);
        try { return _store.Read<WorktreeTaskList>(path); }
        catch (JsonException) { return new WorktreeTaskList(CurrentVersion, []); }
    }

    private IReadOnlyList<LegacyImportDiagnostic> ImportLegacy(string root, EngineeringGraphService graph, string worktreeId)
    {
        var diagnostics = new List<LegacyImportDiagnostic>();
        foreach (var task in LoadLegacy(root).Tasks)
        {
            var metadata = new { priority = 0, intent = "Legacy task", expectedResult = "Unspecified" };
            try
            {
                graph.ImportTask(new GraphTask(task.TaskId, graph.WorkbenchId(), GraphTaskScopeKind.Worktree, worktreeId, task.Title,
                    GraphTaskType.Feature, ToGraphStatus(task.Status), task.Details, null, task.CreatedUtc, task.CreatedUtc, metadata.priority, metadata.intent, metadata.expectedResult),
                    task.ElementRefs ?? [], task.DoneUtc, $"{worktreeId}:{task.TaskId}");
            }
            catch (EngineeringGraphConstraintException exception)
            {
                diagnostics.Add(new LegacyImportDiagnostic(task.TaskId, worktreeId, exception.Message));
            }
        }
        return diagnostics;
    }

    private void ImportRegisteredWorktrees(EngineeringGraphService graph, WorkbenchMetadata workbench)
    {
        var diagnostics = new List<LegacyImportDiagnostic>();
        foreach (var registration in workbench.Worktrees)
        {
            var root = WorkbenchPaths.ResolveWorktree(workbench.RootPath, registration.RelativePath);
            if (File.Exists(Path.Combine(root, "worktree.json")))
                diagnostics.AddRange(ImportLegacy(root, graph, registration.WorktreeId));
        }
        LastImportDiagnostics = diagnostics;
    }

    private bool TryOpenGraph(string root, out EngineeringGraphService graph, out EngineeringGraphStore graphStore, out string? worktreeId, out WorkbenchMetadata? workbench)
    {
        graph = null!; graphStore = null!; worktreeId = null; workbench = null;
        try
        {
            var worktreePath = Path.Combine(root, "worktree.json");
            if (!File.Exists(worktreePath)) return false;
            WorktreeMetadata metadata;
            try { metadata = _store.Read<WorktreeMetadata>(worktreePath); }
            catch (JsonException) { return false; }
            catch (MetadataSchemaException) { return false; }
            var current = new DirectoryInfo(root);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "workbench.json"))) current = current.Parent;
            if (current is null) return false;
            var loadedWorkbench = _store.Read<WorkbenchMetadata>(Path.Combine(current.FullName, "workbench.json"));
            workbench = loadedWorkbench;
            graphStore = new EngineeringGraphStore(current.FullName);
            var resolvedWorkbench = loadedWorkbench;
            graph = new EngineeringGraphService(graphStore, resolvedWorkbench.WorkbenchId, id => resolvedWorkbench.Worktrees.Any(w => w.WorktreeId == id));
            worktreeId = metadata.WorktreeId;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or MetadataSchemaException)
        {
            graphStore?.Dispose();
            throw new InvalidOperationException("Engineering graph initialization failed.", exception);
        }
    }

    private static WorktreeTask ToLegacy(GraphTask task, string[]? refs = null)
    {
        var parsed = task.MetadataJson is null ? null : JsonSerializer.Deserialize<LegacyMetadata>(task.MetadataJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return new WorktreeTask(task.TaskId, task.Title, task.Description, ToLegacyStatus(task.Status), refs ?? parsed?.ElementRefs ?? [], task.CreatedUtc!.Value, parsed?.DoneUtc);
    }
    private static GraphTask ToGraph(WorktreeTask task, GraphTask current) => current with
    {
        Title = task.Title, Description = task.Details, Status = ToGraphStatus(task.Status),
        MetadataJson = JsonSerializer.Serialize(new { priority = current.Priority, intent = current.Intent, expectedResult = current.ExpectedResult, elementRefs = task.ElementRefs, doneUtc = task.DoneUtc })
    };
    private static GraphTaskStatus ToGraphStatus(WorktreeTaskStatus s) => s switch { WorktreeTaskStatus.Done => GraphTaskStatus.Done, WorktreeTaskStatus.InProgress => GraphTaskStatus.InProgress, _ => GraphTaskStatus.Todo };
    private static WorktreeTaskStatus ToLegacyStatus(GraphTaskStatus s) => s switch { GraphTaskStatus.Done => WorktreeTaskStatus.Done, GraphTaskStatus.InProgress => WorktreeTaskStatus.InProgress, _ => WorktreeTaskStatus.Todo };
    private sealed record LegacyMetadata(int Priority, string Intent, string ExpectedResult, string[]? ElementRefs, DateTimeOffset? DoneUtc);
}
