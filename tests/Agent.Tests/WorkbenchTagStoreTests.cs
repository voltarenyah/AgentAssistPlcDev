using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class WorkbenchTagStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"workbench-tags-{Guid.NewGuid():N}",
        "tags.json");

    [Fact]
    public void RestartPreservesStableNodeIdsAndAssignments()
    {
        var first = new WorkbenchTagStore(_path);
        var node = new TagNode("tag-1", null, "Production", "production");
        var assignment = new TagAssignment("tag-1", TagEntityType.Workbench, "wb-1", null);

        first.Mutate(document => document with
        {
            Nodes = [node],
            Assignments = [assignment],
        });

        var restarted = new WorkbenchTagStore(_path);
        var loaded = restarted.Load();

        Assert.Equal(WorkbenchTagStore.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal([node], loaded.Nodes);
        Assert.Equal([assignment], loaded.Assignments);
    }

    [Fact]
    public void CorruptDocumentRaisesDefinedErrorAndIsNotReset()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        const string corrupt = "{not-json";
        File.WriteAllText(_path, corrupt);

        var error = Assert.Throws<WorkbenchTagStoreException>(
            () => new WorkbenchTagStore(_path).Load());

        Assert.Contains("could not be read", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(corrupt, File.ReadAllText(_path));
    }

    [Fact]
    public void UnsupportedDocumentRaisesDefinedErrorAndIsNotReset()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        const string unsupported = "{\"schemaVersion\":\"9.9\",\"nodes\":[],\"assignments\":[]}";
        File.WriteAllText(_path, unsupported);

        var error = Assert.Throws<WorkbenchTagStoreException>(
            () => new WorkbenchTagStore(_path).Load());

        Assert.Contains("not supported", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(unsupported, File.ReadAllText(_path));
    }

    [Fact]
    public void NumericEntityTypeRaisesDefinedErrorAndIsNotReset()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        const string numeric = "{\"schemaVersion\":\"1.0\",\"nodes\":[],\"assignments\":[{\"tagId\":\"tag-1\",\"entityType\":999,\"entityId\":\"wb-1\",\"workbenchId\":null}]}";
        File.WriteAllText(_path, numeric);

        var error = Assert.Throws<WorkbenchTagStoreException>(
            () => new WorkbenchTagStore(_path).Load());

        Assert.Contains("could not be read", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(numeric, File.ReadAllText(_path));
    }

    [Fact]
    public async Task ConcurrentMutationsAreSerializedWithoutLostUpdates()
    {
        var store = new WorkbenchTagStore(_path);
        var mutations = Enumerable.Range(0, 12)
            .Select(index => Task.Run(() => store.Mutate(document => document with
            {
                Nodes = [.. document.Nodes, new TagNode($"tag-{index}", null, $"Tag {index}", $"tag {index}")],
            })))
            .ToArray();

        await Task.WhenAll(mutations);

        Assert.Equal(12, new WorkbenchTagStore(_path).Load().Nodes.Count);
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
