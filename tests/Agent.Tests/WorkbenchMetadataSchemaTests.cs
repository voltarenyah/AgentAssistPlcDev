using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class WorkbenchMetadataSchemaTests : IDisposable
{
    private readonly string _testRoot =
        Path.Combine(Path.GetTempPath(), $"workbench-metadata-schema-tests-{Guid.NewGuid():N}");

    [Fact]
    public void WorkbenchMetadataRoundTripsLandingPageFields()
    {
        var store = new AtomicJsonStore();
        var path = Path.Combine(_testRoot, "workbench.json");
        var metadata = new WorkbenchMetadata(
            WorkbenchSchema.CurrentVersion,
            "wb-1",
            "Line 1",
            "2026-08-01T00:00:00.0000000Z",
            _testRoot,
            Path.Combine(_testRoot, "repository.git"),
            null,
            null,
            Array.Empty<WorkbenchWorktreeRegistration>(),
            Purpose: "Ramp-up line for plant B",
            Owner: "Ansel");

        store.Write(path, metadata);
        var loaded = store.Read<WorkbenchMetadata>(path);

        Assert.Equal(metadata.WorkbenchId, loaded.WorkbenchId);
        Assert.Equal("1.1", loaded.SchemaVersion);
        Assert.Equal("Ramp-up line for plant B", loaded.Purpose);
        Assert.Equal("Ansel", loaded.Owner);
        var persisted = File.ReadAllText(path);
        Assert.Contains("\"purpose\": \"Ramp-up line for plant B\"", persisted);
        Assert.Contains("\"owner\": \"Ansel\"", persisted);
    }

    [Fact]
    public void WorktreeMetadataRoundTripsLandingPageFieldsAsCamelCaseStrings()
    {
        var store = new AtomicJsonStore();
        var path = Path.Combine(_testRoot, "worktree.json");
        var finished = new DateTimeOffset(2026, 8, 2, 12, 30, 0, TimeSpan.Zero);
        var metadata = new WorktreeMetadata(
            WorkbenchSchema.CurrentVersion,
            "wt-1",
            "wb-1",
            "Feature A",
            "feature/a",
            "2026-08-01T00:00:00.0000000Z",
            null,
            null,
            null,
            new[] { "dev-1" },
            null,
            Purpose: "Cylinder retrofit",
            Owner: "Ansel",
            Status: WorktreeStatus.Finished,
            FinishedUtc: finished);

        store.Write(path, metadata);
        var loaded = store.Read<WorktreeMetadata>(path);

        Assert.Equal(metadata.WorktreeId, loaded.WorktreeId);
        Assert.Equal("Cylinder retrofit", loaded.Purpose);
        Assert.Equal("Ansel", loaded.Owner);
        Assert.Equal(WorktreeStatus.Finished, loaded.Status);
        Assert.Equal(finished, loaded.FinishedUtc);
        Assert.Equal(new[] { "dev-1" }, loaded.DeviceIds);
        var persisted = File.ReadAllText(path);
        Assert.Contains("\"status\": \"finished\"", persisted);
        Assert.Contains("\"purpose\": \"Cylinder retrofit\"", persisted);
    }

    [Fact]
    public void Schema10WorkbenchFileLoadsWithDefaultLandingPageFields()
    {
        var store = new AtomicJsonStore();
        var path = Path.Combine(_testRoot, "workbench.json");
        Directory.CreateDirectory(_testRoot);
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": "1.0",
              "workbenchId": "wb-1",
              "name": "Line 1",
              "createdAt": "2026-07-27T00:00:00.0000000Z",
              "rootPath": {{System.Text.Json.JsonSerializer.Serialize(_testRoot)}},
              "repositoryPath": "repo.git",
              "engineeringProjectId": null,
              "sourceProjectPath": null,
              "worktrees": []
            }
            """);

        var loaded = store.Read<WorkbenchMetadata>(path);

        Assert.Equal("1.0", loaded.SchemaVersion);
        Assert.Null(loaded.Purpose);
        Assert.Null(loaded.Owner);
    }

    [Fact]
    public void Schema10WorktreeFileLoadsWithDefaultLandingPageFields()
    {
        var store = new AtomicJsonStore();
        var path = Path.Combine(_testRoot, "worktree.json");
        Directory.CreateDirectory(_testRoot);
        File.WriteAllText(path, """
            {
              "schemaVersion": "1.0",
              "worktreeId": "wt-1",
              "workbenchId": "wb-1",
              "name": "master",
              "branch": "master",
              "createdAt": "2026-07-27T00:00:00.0000000Z",
              "baseCommit": null,
              "engineeringProjectId": null,
              "sourceProjectPath": null,
              "deviceIds": [],
              "lastReconciliationCommit": null
            }
            """);

        var loaded = store.Read<WorktreeMetadata>(path);

        Assert.Equal("1.0", loaded.SchemaVersion);
        Assert.Null(loaded.Purpose);
        Assert.Null(loaded.Owner);
        Assert.Equal(WorktreeStatus.Ongoing, loaded.Status);
        Assert.Null(loaded.FinishedUtc);
    }

    [Fact]
    public void UnsupportedSchemaVersionIsStillRejected()
    {
        var store = new AtomicJsonStore();
        var path = Path.Combine(_testRoot, "workbench.json");
        Directory.CreateDirectory(_testRoot);
        File.WriteAllText(path, """{"schemaVersion":"2.0"}""");

        var error = Assert.Throws<MetadataSchemaException>(
            () => store.Read<WorkbenchMetadata>(path));

        Assert.Equal("2.0", error.ActualVersion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
