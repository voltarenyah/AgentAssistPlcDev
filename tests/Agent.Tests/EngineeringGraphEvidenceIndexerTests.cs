using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Xunit;

namespace Agent.Tests;

public sealed class EngineeringGraphEvidenceIndexerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"engineering-graph-evidence-{Guid.NewGuid():N}");

    [Fact]
    public void ResolvesManifestSourceAndPreservesUnresolvedFiles()
    {
        var registration = new WorkbenchWorktreeRegistration("wt-1", "Worktree", "main", "worktree");
        var workbench = new WorkbenchMetadata("1.2", "wb-1", "Workbench", DateTimeOffset.UtcNow.ToString("O"),
            root, root, null, null, [registration]);
        var worktreeRoot = Path.Combine(root, "worktrees", "worktree");
        Directory.CreateDirectory(Path.Combine(worktreeRoot, "devices", "dev-1", "source"));
        var metadataStore = new AtomicJsonStore();
        metadataStore.Write(Path.Combine(worktreeRoot, "worktree.json"),
            new WorktreeMetadata("1.2", "wt-1", "wb-1", "Worktree", "main",
                DateTimeOffset.UtcNow.ToString("O"), null, null, null, ["dev-1"], null));
        metadataStore.Write(Path.Combine(worktreeRoot, "devices", "dev-1", "device.json"),
            new DeviceMetadata("1.2", "dev-1", "wt-1", "dev-1", "engineering", null, null, null,
                new KnowledgeState(false, new Dictionary<string, string>(), null), []));
        File.WriteAllText(Path.Combine(worktreeRoot, "devices", "dev-1", "source", "metadata.json"),
            """{"components":[{"id":"block-1","name":"Main","category":"Blocks","exportedFile":"Blocks/Main.xml"}]}""");
        using var store = new EngineeringGraphStore(root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var result = new EngineeringGraphEvidenceIndexer(graph, workbench).IndexCommit("wt-1",
            new VersionControlTimelineGitCommit("commit-1", "author", "message", "2026-01-01",
                ["devices/dev-1/source/Blocks/Main.xml", "unknown.txt"], null, null, false));

        var edge = Assert.Single(result.SourceEdges);
        Assert.Equal(GraphProvenance.Evidence, edge.Provenance);
        Assert.Equal("dev-1:block-1", edge.ToId);
        var unresolved = Assert.Single(result.UnresolvedFiles);
        Assert.Equal("unknown.txt", unresolved.RelativePath);
        var raw = Assert.Single(graph.GetFileEvidence("commit-1"));
        Assert.Equal("unknown.txt", raw.RelativePath);
    }

    [Fact]
    public void LinksSavepointOnlyFromExistingTimelineRevisionEvidence()
    {
        var registration = new WorkbenchWorktreeRegistration("wt-1", "Worktree", "main", "worktree");
        var workbench = new WorkbenchMetadata("1.2", "wb-1", "Workbench", DateTimeOffset.UtcNow.ToString("O"),
            root, root, null, null, [registration]);
        var worktreeRoot = Path.Combine(root, "worktrees", "worktree");
        Directory.CreateDirectory(worktreeRoot);
        new AtomicJsonStore().Write(Path.Combine(worktreeRoot, "worktree.json"),
            new WorktreeMetadata("1.2", "wt-1", "wb-1", "Worktree", "main",
                DateTimeOffset.UtcNow.ToString("O"), null, null, null, [], null));
        using var store = new EngineeringGraphStore(root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var result = new EngineeringGraphEvidenceIndexer(graph, workbench).IndexCommit("wt-1",
            new VersionControlTimelineGitCommit("savepoint-git", "author", "savepoint", "2026-01-01",
                [EngineeringStateWriter.RelativePath], null, 42, false));

        var edge = Assert.Single(result.SvnEdges);
        Assert.Equal("wt-1:42", edge.ToId);
        Assert.Equal(GraphProvenance.Evidence, edge.Provenance);
        Assert.Empty(result.SourceEdges);
        Assert.Empty(result.UnresolvedFiles);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
