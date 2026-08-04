using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class AtomicJsonStoreTests : IDisposable
{
    private readonly string _testRoot =
        Path.Combine(Path.GetTempPath(), $"atomic-json-store-tests-{Guid.NewGuid():N}");

    [Fact]
    public void WriteThenReadRoundTripsWorkbenchMetadata()
    {
        var store = new AtomicJsonStore();
        var path = Path.Combine(_testRoot, "workbench.json");
        var metadata = CreateMetadata(_testRoot);

        store.Write(path, metadata);
        var loaded = store.Read<WorkbenchMetadata>(path);

        Assert.Equal(metadata.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(metadata.WorkbenchId, loaded.WorkbenchId);
        Assert.Equal(metadata.Name, loaded.Name);
        Assert.Equal(metadata.CreatedAt, loaded.CreatedAt);
        Assert.Equal(metadata.RootPath, loaded.RootPath);
        Assert.Equal(metadata.RepositoryPath, loaded.RepositoryPath);
        Assert.Equal(metadata.EngineeringProjectId, loaded.EngineeringProjectId);
        Assert.Equal(metadata.SourceProjectPath, loaded.SourceProjectPath);
        Assert.Equal(metadata.Worktrees, loaded.Worktrees);
        Assert.Contains(
            Environment.NewLine + "  \"schemaVersion\": \"1.1\"",
            File.ReadAllText(path));
    }

    [Fact]
    public void TryReadReturnsNullWhenMetadataDoesNotExist()
    {
        var store = new AtomicJsonStore();

        var loaded = store.TryRead<WorkbenchMetadata>(
            Path.Combine(_testRoot, "missing.json"));

        Assert.Null(loaded);
        Assert.False(Directory.Exists(_testRoot));
    }

    [Fact]
    public void UnsupportedSchemaDoesNotGetOverwritten()
    {
        var store = new AtomicJsonStore();
        var path = Path.Combine(_testRoot, "workbench.json");
        const string unsupported = """{"schemaVersion":"99.0"}""";
        Directory.CreateDirectory(_testRoot);
        File.WriteAllText(path, unsupported);

        var error = Assert.Throws<MetadataSchemaException>(
            () => store.Read<WorkbenchMetadata>(path));

        Assert.Equal("99.0", error.ActualVersion);
        Assert.Equal(unsupported, File.ReadAllText(path));
    }

    [Fact]
    public void SerializationFailurePreservesExistingMetadataAndCleansTemporaryFile()
    {
        var store = new AtomicJsonStore();
        var path = Path.Combine(_testRoot, "workbench.json");
        Directory.CreateDirectory(_testRoot);
        File.WriteAllText(path, "existing metadata");

        Assert.Throws<NotSupportedException>(
            () => store.Write(path, new UnsupportedSerializationValue(() => { })));

        Assert.Equal("existing metadata", File.ReadAllText(path));
        Assert.Empty(EnumerateTemporaryFiles(path));
    }

    [Fact]
    public void ReplaceFailurePreservesExistingMetadataAndCleansTemporaryFile()
    {
        var store = new AtomicJsonStore();
        var path = Path.Combine(_testRoot, "workbench.json");
        Directory.CreateDirectory(_testRoot);
        File.WriteAllText(path, "existing metadata");

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(
                () => store.Write(path, CreateMetadata(_testRoot)));
        }

        Assert.Equal("existing metadata", File.ReadAllText(path));
        Assert.Empty(EnumerateTemporaryFiles(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static WorkbenchMetadata CreateMetadata(string root) =>
        new(
            WorkbenchSchema.CurrentVersion,
            "wb-1",
            "Line 1",
            "2026-07-27T00:00:00.0000000Z",
            root,
            Path.Combine(root, "repository.git"),
            "eng-1",
            @"D:\TIA\Line1.ap17",
            Array.Empty<WorkbenchWorktreeRegistration>());

    private static string[] EnumerateTemporaryFiles(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var pattern = $".{Path.GetFileName(path)}.*.tmp";
        return Directory.GetFiles(directory, pattern);
    }

    private sealed record UnsupportedSerializationValue(Action Callback);
}
