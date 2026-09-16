using Microsoft.Data.Sqlite;

namespace Agent.Workbench.EngineeringGraph;

public sealed class EngineeringGraphCommitAttributionProvider
{
    private readonly ActiveTaskContextService activeTasks;

    public EngineeringGraphCommitAttributionProvider(ActiveTaskContextService activeTasks)
    {
        this.activeTasks = activeTasks ?? throw new ArgumentNullException(nameof(activeTasks));
    }

    public string? Associate(
        WorkbenchMetadata workbench,
        string worktreeId,
        string evidenceId,
        IReadOnlyList<string>? additionalTaskIds = null)
    {
        try
        {
            using var store = new EngineeringGraphStore(workbench.RootPath);
            var graph = new EngineeringGraphService(
                store,
                workbench.WorkbenchId,
                id => workbench.Worktrees.Any(item => item.WorktreeId == id));
            return new EngineeringGraphCommitAttribution(graph, activeTasks).Associate(
                workbench.WorkbenchId, worktreeId, evidenceId,
                additionalTaskIds: additionalTaskIds);
        }
        catch (Exception exception) when (IsPersistenceFailure(exception))
        {
            return $"Commit '{evidenceId}' succeeded, but task attribution was not recorded: {exception.Message} Repair the attribution from the task traceability view.";
        }
    }

    private static bool IsPersistenceFailure(Exception exception) =>
        exception is SqliteException or IOException or UnauthorizedAccessException;
}

/// <summary>Associates an app-created Git commit with task intent after the commit exists.</summary>
public sealed class EngineeringGraphCommitAttribution
{
    private readonly EngineeringGraphService graph;
    private readonly ActiveTaskContextService activeTasks;

    public EngineeringGraphCommitAttribution(
        EngineeringGraphService graph,
        ActiveTaskContextService? activeTasks = null)
    {
        this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
        this.activeTasks = activeTasks ?? new ActiveTaskContextService();
    }

    /// <summary>
    /// Adds the selected task as the default (primary) association and any explicit task IDs as
    /// manual secondary associations. The commit is registered before edges are written so a
    /// failed graph write leaves the stable evidence identity available for repair.
    /// </summary>
    public string? Associate(
        string workbenchId,
        string worktreeId,
        string evidenceId,
        string? activeTaskId = null,
        IReadOnlyList<string>? additionalTaskIds = null)
    {
        if (string.IsNullOrWhiteSpace(evidenceId))
            throw new ArgumentException("A Git evidence ID is required.", nameof(evidenceId));
        try
        {
            if (!string.Equals(workbenchId, graph.WorkbenchId(), StringComparison.Ordinal))
                throw new EngineeringGraphConstraintException("Commit evidence belongs to another Workbench.");

            graph.RegisterEntity(new GraphEntity(
                GraphEntityKind.GitCommit, evidenceId, workbenchId, worktreeId));

            var selected = activeTaskId is null
                ? activeTasks.Get(graph, worktreeId)?.TaskId
                : activeTaskId;
            var existing = graph.GetIncomingEdges(GraphEntityKind.GitCommit, evidenceId);
            if (!string.IsNullOrWhiteSpace(selected))
            {
                var selectedEdge = existing.FirstOrDefault(edge => edge.FromId == selected);
                if (selectedEdge is null)
                    graph.AddEdge(GraphEntityKind.Task, selected, GraphEntityKind.GitCommit, evidenceId,
                        GraphProvenance.Default, isPrimary: true);
                else if (!selectedEdge.IsPrimary)
                    throw new EngineeringGraphConstraintException("The selected task relationship is not primary.");
            }

            foreach (var taskId in (additionalTaskIds ?? Array.Empty<string>())
                         .Where(id => !string.IsNullOrWhiteSpace(id))
                         .Distinct(StringComparer.Ordinal))
            {
                if (string.Equals(taskId, selected, StringComparison.Ordinal))
                    continue;
                if (!existing.Any(edge => edge.FromId == taskId))
                    graph.AddEdge(GraphEntityKind.Task, taskId, GraphEntityKind.GitCommit, evidenceId,
                        GraphProvenance.Manual, isPrimary: false);
            }
            return null;
        }
        catch (Exception exception) when (exception is EngineeringGraphConstraintException or ObjectDisposedException)
        {
            return $"Commit '{evidenceId}' succeeded, but task attribution was not recorded: {exception.Message} Repair the attribution from the task traceability view.";
        }
    }
}
