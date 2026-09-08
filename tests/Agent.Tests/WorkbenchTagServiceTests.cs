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

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_path);
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
