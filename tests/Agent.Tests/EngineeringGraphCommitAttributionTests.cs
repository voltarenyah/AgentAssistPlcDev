using Agent.Workbench.EngineeringGraph;
using Xunit;

namespace Agent.Tests;

public sealed class EngineeringGraphCommitAttributionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"engineering-graph-attribution-{Guid.NewGuid():N}");

    [Fact]
    public void AssociatesDefaultAndAdditionalTasksInStableOrder()
    {
        using var store = new EngineeringGraphStore(root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var first = graph.CreateTask("task-1", GraphTaskScopeKind.Worktree, "wt-1", "Primary", GraphTaskType.Feature, intent: "intent", expectedResult: "result");
        graph.CreateTask("task-2", GraphTaskScopeKind.Worktree, "wt-1", "Additional", GraphTaskType.Issue, intent: "intent", expectedResult: "result");
        graph.CreateTask("task-3", GraphTaskScopeKind.Worktree, "wt-1", "Additional 2", GraphTaskType.Improvement, intent: "intent", expectedResult: "result");
        var active = new ActiveTaskContextService();
        active.Select(graph, "wt-1", first.TaskId);

        var warning = new EngineeringGraphCommitAttribution(graph, active).Associate(
            "wb-1", "wt-1", "commit-1", additionalTaskIds: ["task-2", "task-3"]);

        Assert.Null(warning);
        var edges = graph.GetIncomingEdges(GraphEntityKind.GitCommit, "commit-1");
        Assert.Equal(["task-1", "task-2", "task-3"], edges.Select(edge => edge.FromId).ToArray());
        Assert.True(edges[0].IsPrimary);
        Assert.Equal(GraphProvenance.Default, edges[0].Provenance);
        Assert.All(edges.Skip(1), edge => Assert.Equal(GraphProvenance.Manual, edge.Provenance));
    }

    [Fact]
    public void RejectsSecondPrimaryWithoutInvalidatingExistingCommitAssociation()
    {
        using var store = new EngineeringGraphStore(root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        graph.CreateTask("task-1", GraphTaskScopeKind.Project, null, "First", GraphTaskType.Feature, intent: "intent", expectedResult: "result");
        graph.CreateTask("task-2", GraphTaskScopeKind.Project, null, "Second", GraphTaskType.Feature, intent: "intent", expectedResult: "result");
        var attribution = new EngineeringGraphCommitAttribution(graph);

        Assert.Null(attribution.Associate("wb-1", "wt-1", "commit-1", "task-1"));
        var warning = attribution.Associate("wb-1", "wt-1", "commit-1", "task-2");

        Assert.Contains("commit-1", warning);
        var edge = Assert.Single(graph.GetIncomingEdges(GraphEntityKind.GitCommit, "commit-1"));
        Assert.Equal("task-1", edge.FromId);
        Assert.True(edge.IsPrimary);
    }

    [Fact]
    public void GraphFailureReturnsRepairableWarningWithStableEvidenceId()
    {
        using var store = new EngineeringGraphStore(root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        graph.CreateTask("task-1", GraphTaskScopeKind.Worktree, "wt-1", "Task", GraphTaskType.Feature, intent: "intent", expectedResult: "result");
        var attribution = new EngineeringGraphCommitAttribution(graph);
        Assert.Null(attribution.Associate("wb-1", "wt-1", "prior-commit", "task-1"));

        using (var command = store.Connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_graph_edge
                BEFORE INSERT ON graph_edges
                WHEN NEW.to_id = 'stable-commit'
                BEGIN
                    SELECT RAISE(ABORT, 'Injected graph edge failure.');
                END;
                """;
            command.ExecuteNonQuery();
        }

        var warning = attribution.Associate("wb-1", "wt-1", "stable-commit", "task-1");

        Assert.Contains("stable-commit", warning);
        Assert.Contains("Repair", warning);

        store.Dispose();
        using var reopenedStore = new EngineeringGraphStore(root);
        var reopenedGraph = new EngineeringGraphService(reopenedStore, "wb-1", id => id == "wt-1");
        Assert.Equal("Task", reopenedGraph.FindTask("task-1")!.Title);
        Assert.NotNull(reopenedGraph.GetEntity(GraphEntityKind.GitCommit, "stable-commit"));
        var edges = reopenedGraph.GetEdges(GraphEntityKind.Task, "task-1", GraphEntityKind.GitCommit);
        var prior = Assert.Single(edges);
        Assert.Equal("prior-commit", prior.ToId);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
