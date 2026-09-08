using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class WorkbenchTagServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"workbench-tag-service-{Guid.NewGuid():N}", "tags.json");

    [Fact]
    public void CreatePathIsDeepAndIdempotent()
    {
        var service = new WorkbenchTagService(new WorkbenchTagStore(_path));

        var first = service.CreatePath(" machine / press / hydraulic / 500t ");
        var second = service.CreatePath("machine/press/hydraulic/500t");

        Assert.Equal(first.TagId, second.TagId);
        var document = new WorkbenchTagStore(_path).Load();
        Assert.Equal(4, document.Nodes.Count);
        Assert.Equal("machine/press/hydraulic/500t", service.GetPath(first.TagId));
        Assert.Equal("machine", document.Nodes[0].Name);
    }

    [Fact]
    public void EmptyInteriorSegmentAndSiblingCollisionAreRejected()
    {
        var service = new WorkbenchTagService(new WorkbenchTagStore(_path));
        var first = service.CreatePath("Machine/Press");
        var repeated = service.CreatePath(" machine / PRESS ");

        Assert.Equal(first.TagId, repeated.TagId);
        var stored = new WorkbenchTagStore(_path).Load();
        Assert.Equal(["Machine", "Press"], stored.Nodes.Select(node => node.Name));

        var empty = Assert.Throws<WorkbenchTagDomainException>(() => service.CreatePath("Machine//Hydraulic"));
        Assert.Equal("invalid_path", empty.Code);

        var collision = Assert.Throws<WorkbenchTagDomainException>(() => service.Rename(
            service.CreatePath("Machine/Valve").TagId,
            " press "));
        Assert.Equal("sibling_name_conflict", collision.Code);
    }

    [Fact]
    public void RenamePreservesIdsAssignmentsAndChangesDerivedDescendantPaths()
    {
        var store = new WorkbenchTagStore(_path);
        var service = new WorkbenchTagService(store);
        var leaf = service.CreatePath("machine/press");
        var machine = store.Load().Nodes.Single(node => node.Name == "machine");
        store.Mutate(document => document with
        {
            Assignments = [new TagAssignment(leaf.TagId, TagEntityType.Workbench, "wb-1", null)],
        });

        service.Rename(machine.TagId, " Line ");

        var document = store.Load();
        Assert.Equal(new[] { machine.TagId, leaf.TagId }, document.Nodes.Select(node => node.TagId));
        Assert.Equal(" Line ".Trim(), document.Nodes.Single(node => node.TagId == machine.TagId).Name);
        Assert.Equal("Line/press", service.GetPath(leaf.TagId));
        Assert.Equal("wb-1", Assert.Single(document.Assignments).EntityId);
    }

    [Fact]
    public void DeleteRejectsAssignedAndParentNodesButAllowsUnassignedLeaf()
    {
        var store = new WorkbenchTagStore(_path);
        var service = new WorkbenchTagService(store);
        var assigned = service.CreatePath("assigned");
        store.Mutate(document => document with
        {
            Assignments = [new TagAssignment(assigned.TagId, TagEntityType.Workbench, "wb-1", null)],
        });

        var assignedError = Assert.Throws<WorkbenchTagDomainException>(() => service.Delete(assigned.TagId));
        Assert.Equal("tag_assigned", assignedError.Code);

        var parent = service.CreatePath("parent/child");
        var parentError = Assert.Throws<WorkbenchTagDomainException>(() => service.Delete(parent.ParentTagId!));
        Assert.Equal("tag_has_children", parentError.Code);

        service.Delete(parent.TagId);
        Assert.DoesNotContain(store.Load().Nodes, node => node.TagId == parent.TagId);
    }

    [Fact]
    public void AssignmentsValidateCatalogAndEffectiveWorktreeTagsAreComputedAtReadTime()
    {
        var metadata = new WorkbenchMetadata(
            WorkbenchSchema.CurrentVersion, "wb-1", "Fixture", "now", "/tmp/wb", "/tmp/repo", null, null,
            [new WorkbenchWorktreeRegistration("wt-1", "Feature", "feature", "worktrees/feature")]);
        var service = new WorkbenchTagService(
            new WorkbenchTagStore(_path),
            new WorkbenchCatalogTagEntityLookup([metadata]));
        var workbenchTag = service.CreatePath("area/assembly");
        var worktreeTag = service.CreatePath("priority/high");

        service.AssignWorkbenchTag(workbenchTag.TagId, "wb-1");
        service.AssignWorkbenchTag(workbenchTag.TagId, "wb-1");
        service.AssignWorktreeTag(worktreeTag.TagId, "wb-1", "wt-1");
        service.AssignWorktreeTag(worktreeTag.TagId, "wb-1", "wt-1");

        var projection = service.GetWorktreeTags("wb-1", "wt-1");
        Assert.Equal([worktreeTag.TagId], projection.DirectTagIds);
        Assert.Equal(new[] { workbenchTag.TagId, worktreeTag.TagId }, projection.EffectiveTagIds);
        Assert.Equal(2, new WorkbenchTagStore(_path).Load().Assignments.Count);

        service.UnassignWorkbenchTag(workbenchTag.TagId, "wb-1");
        service.UnassignWorkbenchTag(workbenchTag.TagId, "wb-1");
        Assert.Equal([worktreeTag.TagId], service.GetWorktreeTags("wb-1", "wt-1").EffectiveTagIds);
        Assert.Single(new WorkbenchTagStore(_path).Load().Assignments);

        service.UnassignWorktreeTag(worktreeTag.TagId, "wb-1", "wt-1");
        service.UnassignWorktreeTag(worktreeTag.TagId, "wb-1", "wt-1");
        Assert.Empty(service.GetWorktreeTags("wb-1", "wt-1").DirectTagIds);
        Assert.Empty(service.GetWorktreeTags("wb-1", "wt-1").EffectiveTagIds);
        Assert.Empty(new WorkbenchTagStore(_path).Load().Assignments);

        var mismatch = Assert.Throws<WorkbenchTagDomainException>(() =>
            service.AssignWorktreeTag(worktreeTag.TagId, "wb-other", "wt-1"));
        Assert.Equal("worktree_not_found", mismatch.Code);
    }

    [Fact]
    public void CatalogAdapterResolvesRegisteredWorktreeAndRejectsMismatchedOwner()
    {
        var catalogRoot = Path.Combine(Path.GetTempPath(), $"workbench-tag-catalog-{Guid.NewGuid():N}");
        try
        {
            var catalog = new WorkbenchCatalog(new AtomicJsonStore(), catalogRoot);
            var workbench = catalog.Create("fixture", Path.Combine(catalogRoot, "fixture"));
            var otherWorkbench = catalog.Create("other", Path.Combine(catalogRoot, "other"));
            workbench = catalog.RegisterWorktree(workbench, new WorkbenchWorktreeRegistration(
                "wt-1", "Feature", "feature", "worktrees/feature"));
            var service = new WorkbenchTagService(
                new WorkbenchTagStore(_path),
                new WorkbenchCatalogTagEntityLookup(catalog, [workbench.RootPath, otherWorkbench.RootPath]));
            var tag = service.CreatePath("catalog/verified");

            service.AssignWorktreeTag(tag.TagId, workbench.WorkbenchId, "wt-1");
            Assert.Equal([tag.TagId], service.GetWorktreeTags(workbench.WorkbenchId, "wt-1").EffectiveTagIds);

            var mismatch = Assert.Throws<WorkbenchTagDomainException>(() =>
                service.AssignWorktreeTag(tag.TagId, otherWorkbench.WorkbenchId, "wt-1"));
            Assert.Equal("worktree_not_found", mismatch.Code);
        }
        finally
        {
            if (Directory.Exists(catalogRoot))
            {
                Directory.Delete(catalogRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void SearchExpandsDescendantsAndAppliesAndSemanticsWithoutInferringWorkbenchMatches()
    {
        var root = Path.Combine(Path.GetTempPath(), $"workbench-tag-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var workbenchRoot = Path.Combine(root, "fixture");
            Directory.CreateDirectory(Path.Combine(workbenchRoot, "worktrees", "present"));
            File.WriteAllText(Path.Combine(workbenchRoot, "workbench.json"), "{}");
            File.WriteAllText(Path.Combine(workbenchRoot, "worktrees", "present", "worktree.json"), "{}");
            var metadata = new WorkbenchMetadata(
                WorkbenchSchema.CurrentVersion, "wb-1", "Fixture", "now", workbenchRoot, "repo", null, null,
                [
                    new WorkbenchWorktreeRegistration("wt-present", "Present", "feature", "present"),
                    new WorkbenchWorktreeRegistration("wt-missing", "Missing", "feature", "missing"),
                ]);
            var service = new WorkbenchTagService(
                new WorkbenchTagStore(_path),
                new SearchLookup([metadata]));
            var parent = service.CreatePath("area");
            var child = service.CreatePath("area/assembly");
            var sibling = service.CreatePath("area/packaging");
            var priority = service.CreatePath("priority/high");
            var unassigned = service.CreatePath("unassigned");
            service.AssignWorkbenchTag(child.TagId, "wb-1");
            service.AssignWorktreeTag(sibling.TagId, "wb-1", "wt-present");
            service.AssignWorktreeTag(priority.TagId, "wb-1", "wt-missing");

            var descendant = service.Search([parent.TagId]);
            var workbench = Assert.Single(descendant.Workbenches);
            Assert.Equal([child.TagId], workbench.DirectTagIds);
            Assert.Equal([child.TagId], workbench.EffectiveTagIds);
            Assert.Contains(descendant.Worktrees, result => result.EntityId == "wt-present");

            var and = service.Search([parent.TagId, priority.TagId]);
            var missing = Assert.Single(and.Worktrees);
            Assert.Equal("wt-missing", missing.EntityId);
            Assert.Equal("wb-1", missing.WorkbenchId);
            Assert.False(missing.Available);

            var siblingSearch = service.Search([sibling.TagId]);
            var present = Assert.Single(siblingSearch.Worktrees);
            Assert.Equal("wt-present", present.EntityId);
            Assert.True(present.Available);
            Assert.Empty(siblingSearch.Workbenches);

            var noMatch = service.Search([unassigned.TagId]);
            Assert.Empty(noMatch.Workbenches);
            Assert.Empty(noMatch.Worktrees);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_path);
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SearchLookup(IReadOnlyList<WorkbenchMetadata> workbenches)
        : IWorkbenchTagSearchEntityLookup
    {
        public IReadOnlyList<WorkbenchMetadata> RegisteredWorkbenches { get; } = workbenches;

        public bool WorkbenchExists(string workbenchId) => RegisteredWorkbenches.Any(
            workbench => string.Equals(workbench.WorkbenchId, workbenchId, StringComparison.Ordinal));

        public bool WorktreeBelongsToWorkbench(string workbenchId, string worktreeId) => RegisteredWorkbenches.Any(
            workbench => string.Equals(workbench.WorkbenchId, workbenchId, StringComparison.Ordinal)
                && workbench.Worktrees.Any(worktree => string.Equals(worktree.WorktreeId, worktreeId, StringComparison.Ordinal)));
    }
}
