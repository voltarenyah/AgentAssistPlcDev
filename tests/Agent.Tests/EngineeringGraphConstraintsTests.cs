using Agent.Workbench.EngineeringGraph;
using Xunit;

namespace Agent.Tests;

public sealed class EngineeringGraphConstraintsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"engineering-graph-constraints-{Guid.NewGuid():N}");

    [Fact]
    public void SupportsAllV1RelationPairsAndManyToManyTaskCommits()
    {
        using var store = new EngineeringGraphStore(_root);
        var service = new EngineeringGraphService(store, "wb-1", id => id is "wt-1" or "wt-2");
        var task = service.CreateTask("task-1", GraphTaskScopeKind.Project, null, "Fix", GraphTaskType.Issue, intent: "Because", expectedResult: "Fixed");
        service.RegisterEntity(new GraphEntity(GraphEntityKind.Session, "session-1", "wb-1", null));
        service.RegisterEntity(new GraphEntity(GraphEntityKind.GitCommit, "commit-1", "wb-1", "wt-1"));
        service.RegisterEntity(new GraphEntity(GraphEntityKind.GitCommit, "commit-2", "wb-1", "wt-1"));
        service.RegisterEntity(new GraphEntity(GraphEntityKind.SourceObject, "source-1", "wb-1", "wt-1"));
        service.RegisterEntity(new GraphEntity(GraphEntityKind.SvnRevision, "svn-1", "wb-1", "wt-1"));

        service.AddEdge(task, GraphEntityKind.Session, "session-1");
        service.AddEdge(task, GraphEntityKind.GitCommit, "commit-1", isPrimary: true);
        service.AddEdge(task, GraphEntityKind.GitCommit, "commit-2");
        service.AddEdge(task, GraphEntityKind.SourceObject, "source-1");
        service.AddEdge(task, GraphEntityKind.SvnRevision, "svn-1");
        service.AddEdge(GraphEntityKind.GitCommit, "commit-1", GraphEntityKind.SourceObject, "source-1");
        service.AddEdge(GraphEntityKind.GitCommit, "commit-1", GraphEntityKind.SvnRevision, "svn-1");

        Assert.Equal(7, service.CountEdges());
        Assert.Equal(2, service.GetEdges(GraphEntityKind.Task, "task-1", GraphEntityKind.GitCommit).Count);
    }

    [Fact]
    public void RejectsCrossWorkbenchAndWorktreeRelationshipsWithoutWriting()
    {
        using var store = new EngineeringGraphStore(_root);
        var service = new EngineeringGraphService(store, "wb-1", id => id is "wt-1" or "wt-2");
        var task = service.CreateTask("task-1", GraphTaskScopeKind.Worktree, "wt-1", "Fix", GraphTaskType.Feature, intent: "Because", expectedResult: "Fixed");
        Assert.Throws<EngineeringGraphConstraintException>(() =>
            service.RegisterEntity(new GraphEntity(GraphEntityKind.GitCommit, "commit-1", "wb-2", "wt-1")));
        service.RegisterEntity(new GraphEntity(GraphEntityKind.GitCommit, "commit-2", "wb-1", "wt-2"));

        Assert.Throws<EngineeringGraphConstraintException>(() => service.AddEdge(task, GraphEntityKind.GitCommit, "commit-1"));
        Assert.Throws<EngineeringGraphConstraintException>(() => service.AddEdge(task, GraphEntityKind.GitCommit, "commit-2"));
        Assert.Equal(0, service.CountEdges());
        Assert.Throws<EngineeringGraphConstraintException>(() =>
            service.CreateTask("unknown", GraphTaskScopeKind.Worktree, "wt-unknown", "Unknown", GraphTaskType.Feature));
        Assert.Equal(0, service.GetTask("unknown") is null ? 0 : 1);
    }

    [Fact]
    public void RejectsUnsupportedRelationAndSecondPrimaryWithoutCorruptingExistingEdges()
    {
        using var store = new EngineeringGraphStore(_root);
        var service = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var first = service.CreateTask("task-1", GraphTaskScopeKind.Project, null, "First", GraphTaskType.Issue, priority: 7, intent: "A", expectedResult: "B");
        var second = service.CreateTask("task-2", GraphTaskScopeKind.Project, null, "Second", GraphTaskType.Issue, intent: "A", expectedResult: "B");
        service.RegisterEntity(new GraphEntity(GraphEntityKind.GitCommit, "commit-1", "wb-1", "wt-1"));
        service.RegisterEntity(new GraphEntity(GraphEntityKind.GitCommit, "commit-2", "wb-1", "wt-1"));
        service.AddEdge(first, GraphEntityKind.GitCommit, "commit-1", isPrimary: true);
        Assert.Throws<EngineeringGraphConstraintException>(() =>
            service.AddEdge(GraphEntityKind.GitCommit, "commit-1", GraphEntityKind.Task, "task-1"));
        Assert.Throws<EngineeringGraphConstraintException>(() =>
            service.AddEdge(second, GraphEntityKind.GitCommit, "commit-1", isPrimary: true));
        service.AddEdge(second, GraphEntityKind.GitCommit, "commit-1");
        service.AddEdge(second, GraphEntityKind.GitCommit, "commit-2", isPrimary: true);
        Assert.Equal(3, service.CountEdges());
        store.Dispose();
        using var reopenedStore = new EngineeringGraphStore(_root);
        var reopened = new EngineeringGraphService(reopenedStore, "wb-1", id => id == "wt-1").GetTask("task-1")!;
        Assert.Equal("A", reopened.Intent);
        Assert.Equal("B", reopened.ExpectedResult);
        Assert.Equal(7, reopened.Priority);
    }

    public void Dispose()
    {
        SqliteCleanup();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static void SqliteCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
