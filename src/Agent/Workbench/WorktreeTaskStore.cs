using System.Text.Json;

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

        var list = Load(worktreeRoot);
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

        var list = Load(worktreeRoot);
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
        var list = Load(worktreeRoot);
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
}
