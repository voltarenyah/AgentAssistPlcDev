using System.Collections.Concurrent;

namespace Agent.Workbench.EngineeringGraph;

/// <summary>Holds the active task selection for each current Workbench context.</summary>
public sealed class ActiveTaskContextService
{
    private readonly ConcurrentDictionary<string, string?> _selections = new(StringComparer.Ordinal);

    public GraphTask? Get(EngineeringGraphService graph, string? worktreeId)
    {
        var key = Key(graph.WorkbenchId(), worktreeId);
        if (!_selections.TryGetValue(key, out var taskId) || taskId is null)
            return null;
        var task = graph.FindTask(taskId);
        if (task is null || !IsCompatible(task, worktreeId))
        {
            _selections.TryRemove(key, out _);
            return null;
        }
        return task;
    }

    public GraphTask? Select(EngineeringGraphService graph, string? worktreeId, string? taskId)
    {
        var key = Key(graph.WorkbenchId(), worktreeId);
        if (string.IsNullOrWhiteSpace(taskId))
        {
            _selections.TryRemove(key, out _);
            return null;
        }

        var task = graph.FindTask(taskId)
            ?? throw new EngineeringGraphConstraintException("The selected task was not found in the current Workbench.");
        if (!IsCompatible(task, worktreeId))
            throw new EngineeringGraphConstraintException(
                "The selected task is not compatible with the current project or Workbench context.");

        _selections[key] = task.TaskId;
        return task;
    }

    private static bool IsCompatible(GraphTask task, string? worktreeId) =>
        task.ScopeKind == GraphTaskScopeKind.Project
        || (worktreeId is not null && task.WorktreeId == worktreeId);

    private static string Key(string workbenchId, string? worktreeId) =>
        workbenchId + "\u001f" + (worktreeId ?? string.Empty);
}
