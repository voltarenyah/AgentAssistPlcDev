using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class WorkbenchCatalogTests : IDisposable
{
    private readonly string _testRoot =
        Path.Combine(Path.GetTempPath(), $"workbench-catalog-tests-{Guid.NewGuid():N}");

    [Fact]
    public void CatalogCreateRejectsExistingNonEmptyDirectory()
    {
        var root = Path.Combine(_testRoot, "Line1");
        Directory.CreateDirectory(root);
        var sentinel = Path.Combine(root, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        var catalog = CreateCatalog();

        var error = Assert.Throws<WorkbenchCatalogException>(() =>
            catalog.Create("Line 1", root));

        Assert.Equal("WORKBENCH_CONFLICT", error.Code);
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public void CatalogUsesCustomRootWithoutMovingItUnderDefaultRoot()
    {
        var custom = Path.Combine(_testRoot, "chosen", "Line1");
        var catalog = CreateCatalog();

        var created = catalog.Create("Line 1", custom);

        Assert.Equal(Path.GetFullPath(custom), created.RootPath);
        Assert.True(File.Exists(Path.Combine(custom, "workbench.json")));
        Assert.True(Directory.Exists(Path.Combine(custom, "worktrees")));
        Assert.False(Directory.Exists(created.RepositoryPath));
        Assert.Equal(
            new[] { "workbench.json", "worktrees" },
            Directory.EnumerateFileSystemEntries(custom)
                .Select(Path.GetFileName)
                .OrderBy(name => name)
                .ToArray());
    }

    [Fact]
    public void CatalogUsesInjectedDefaultRootAndListsCreatedWorkbenches()
    {
        var defaultRoot = Path.Combine(_testRoot, "default-projects");
        var catalog = CreateCatalog(defaultRoot);

        var created = catalog.Create("Line:1", requestedRoot: null);
        var listed = catalog.ListDefaultRoot();

        Assert.Equal(
            Path.GetFullPath(Path.Combine(defaultRoot, "Line_1")),
            created.RootPath);
        var loaded = Assert.Single(listed);
        Assert.Equal(created.WorkbenchId, loaded.WorkbenchId);
        Assert.Equal(created.RootPath, loaded.RootPath);
    }

    [Fact]
    public void RegisterWorktreePersistsRegistration()
    {
        var root = Path.Combine(_testRoot, "Line1");
        var catalog = CreateCatalog();
        var created = catalog.Create("Line 1", root);
        var registration = new WorkbenchWorktreeRegistration(
            "wt-1",
            "Feature A",
            "feature/a",
            "feature-a");

        var updated = catalog.RegisterWorktree(created, registration);
        var loaded = catalog.Load(root);

        Assert.Equal(registration, Assert.Single(updated.Worktrees));
        Assert.Equal(registration, Assert.Single(loaded.Worktrees));
    }

    [Fact]
    public void ResolveDeviceUsesRegisteredWorktreeAndStableIds()
    {
        var root = Path.Combine(_testRoot, "Line1");
        var catalog = CreateCatalog();
        var created = catalog.Create("Line 1", root);
        var registration = new WorkbenchWorktreeRegistration(
            "wt-1",
            "Feature A",
            "feature/a",
            "feature-a");
        var updated = catalog.RegisterWorktree(created, registration);
        var worktree = new WorktreeMetadata(
            WorkbenchSchema.CurrentVersion,
            "wt-1",
            updated.WorkbenchId,
            "Feature A",
            "feature/a",
            "2026-07-27T00:00:00.0000000Z",
            null,
            null,
            null,
            new[] { "dev-1" },
            null);
        var device = new DeviceMetadata(
            WorkbenchSchema.CurrentVersion,
            "dev-1",
            "wt-1",
            "PLC:1",
            "engineering-device-1",
            null,
            null,
            null,
            new KnowledgeState(
                false,
                new Dictionary<string, string>(),
                null),
            Array.Empty<DeviceImportRecord>());

        var context = catalog.ResolveDevice(updated, worktree, device);

        Assert.Equal(updated.WorkbenchId, context.WorkbenchId);
        Assert.Equal("wt-1", context.WorktreeId);
        Assert.Equal("dev-1", context.DeviceId);
        Assert.Equal(
            Path.Combine(root, "worktrees", "feature-a", "devices", "PLC_1"),
            context.DeviceRoot);
    }

    [Fact]
    public void DeleteRemovesWholeWorkbenchRootAfterIdentityCheck()
    {
        var root = Path.Combine(_testRoot, "Line1");
        var catalog = CreateCatalog();
        var created = catalog.Create("Line 1", root);
        Directory.CreateDirectory(Path.Combine(root, "repository.git", "objects"));
        Directory.CreateDirectory(Path.Combine(root, "worktrees", "master"));
        var readOnly = Path.Combine(root, "repository.git", "objects", "pack.pack");
        File.WriteAllText(readOnly, "pack");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        catalog.Delete(created);

        Assert.False(Directory.Exists(root));
        var error = Assert.Throws<WorkbenchCatalogException>(() => catalog.Load(root));
        Assert.Equal("WORKBENCH_NOT_FOUND", error.Code);
    }

    [Fact]
    public void DeleteRejectsForeignIdentityAndPreservesRoot()
    {
        var root = Path.Combine(_testRoot, "Line1");
        var catalog = CreateCatalog();
        var created = catalog.Create("Line 1", root);

        var error = Assert.Throws<WorkbenchCatalogException>(() =>
            catalog.Delete(created with { WorkbenchId = "foreign-id" }));

        Assert.Equal("WORKBENCH_RELATIONSHIP_MISMATCH", error.Code);
        Assert.True(File.Exists(Path.Combine(root, "workbench.json")));
    }

    [Fact]
    public void RemoveWorktreeRejectsTheMasterWorktree()
    {
        var root = Path.Combine(_testRoot, "Line1");
        var catalog = CreateCatalog();
        var created = catalog.Create("Line 1", root);
        var registered = catalog.RegisterWorktree(
            created,
            new WorkbenchWorktreeRegistration("master-id", "master", "master", "master"));

        var error = Assert.Throws<WorkbenchCatalogException>(() =>
            catalog.RemoveWorktree(registered, "master-id"));

        Assert.Equal("MASTER_WORKTREE_PROTECTED", error.Code);
        Assert.True(File.Exists(Path.Combine(root, "workbench.json")));
    }

    [Fact]
    public async Task RollbackCreateWaitsForTiaWriteLockToBeReleased()
    {
        var root = Path.Combine(_testRoot, "Line1");
        var catalog = CreateCatalog();
        var created = catalog.Create("Line 1", root);
        var writeLockPath = Path.Combine(root, "worktrees", "master", "tia", "write.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(writeLockPath)!);
        var writeLock = new FileStream(
            writeLockPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(200);
            writeLock.Dispose();
        });

        try
        {
            catalog.RollbackCreate(created);
            await release;
        }
        finally
        {
            writeLock.Dispose();
        }

        Assert.False(File.Exists(Path.Combine(root, "workbench.json")));
        Assert.False(Directory.Exists(Path.Combine(root, "worktrees")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private WorkbenchCatalog CreateCatalog(string? defaultRoot = null) =>
        new(
            new AtomicJsonStore(),
            defaultRoot ?? Path.Combine(_testRoot, "default-projects"));
}
