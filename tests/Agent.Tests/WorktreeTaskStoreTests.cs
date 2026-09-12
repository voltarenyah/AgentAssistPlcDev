using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using System.Text.Json;
using Xunit;

namespace Agent.Tests;

public sealed class WorktreeTaskStoreTests : IDisposable
{
    private readonly string _testRoot =
        Path.Combine(Path.GetTempPath(), $"worktree-task-store-tests-{Guid.NewGuid():N}");

    [Fact]
    public void MissingTasksFileLoadsAsEmptyCurrentVersionList()
    {
        var store = new WorktreeTaskStore(new AtomicJsonStore());

        var list = store.Load(_testRoot);

        Assert.Equal(WorktreeTaskStore.CurrentVersion, list.Version);
        Assert.Empty(list.Tasks);
    }

    [Fact]
    public void CorruptTasksFileLoadsAsEmptyList()
    {
        var store = new WorktreeTaskStore(new AtomicJsonStore());
        Directory.CreateDirectory(_testRoot);
        File.WriteAllText(WorktreeTaskStore.TasksPath(_testRoot), "{ not json");

        var list = store.Load(_testRoot);

        Assert.Equal(WorktreeTaskStore.CurrentVersion, list.Version);
        Assert.Empty(list.Tasks);
    }

    [Fact]
    public void AddCreatesTodoTaskWithGeneratedIdAndRoundTrips()
    {
        var store = new WorktreeTaskStore(new AtomicJsonStore());

        var added = store.Add(
            _testRoot,
            "Adapt FB_Motor_Control",
            "Rework the interlock logic",
            new[] { "Device01/FB_Motor_Control" });
        var list = store.Load(_testRoot);

        var task = Assert.Single(list.Tasks);
        Assert.Equal(added.TaskId, task.TaskId);
        Assert.Equal(added.CreatedUtc, task.CreatedUtc);
        Assert.False(string.IsNullOrWhiteSpace(task.TaskId));
        Assert.Equal("Adapt FB_Motor_Control", task.Title);
        Assert.Equal("Rework the interlock logic", task.Details);
        Assert.Equal(WorktreeTaskStatus.Todo, task.Status);
        Assert.Equal(new[] { "Device01/FB_Motor_Control" }, task.ElementRefs);
        Assert.True(task.CreatedUtc > DateTimeOffset.MinValue);
        Assert.Null(task.DoneUtc);
        var persisted = File.ReadAllText(WorktreeTaskStore.TasksPath(_testRoot));
        Assert.Contains("\"status\": \"todo\"", persisted);
        Assert.Contains("\"version\": 1", persisted);
    }

    [Fact]
    public void AddRejectsBlankTitle()
    {
        var store = new WorktreeTaskStore(new AtomicJsonStore());

        Assert.Throws<ArgumentException>(() => store.Add(_testRoot, "  "));
        Assert.Empty(store.Load(_testRoot).Tasks);
    }

    [Fact]
    public void UpdateSetsDoneUtcOnTransitionToDoneAndClearsItWhenLeavingDone()
    {
        var store = new WorktreeTaskStore(new AtomicJsonStore());
        var added = store.Add(_testRoot, "Task");

        var done = store.Update(_testRoot, added.TaskId, task =>
            task with { Status = WorktreeTaskStatus.Done });
        Assert.NotNull(done);
        Assert.Equal(WorktreeTaskStatus.Done, done.Status);
        Assert.NotNull(done.DoneUtc);

        var reopened = store.Update(_testRoot, added.TaskId, task =>
            task with { Status = WorktreeTaskStatus.InProgress });
        Assert.NotNull(reopened);
        Assert.Equal(WorktreeTaskStatus.InProgress, reopened.Status);
        Assert.Null(reopened.DoneUtc);
    }

    [Fact]
    public void UpdateKeepsDoneUtcWhenDoneTaskChangesTitle()
    {
        var store = new WorktreeTaskStore(new AtomicJsonStore());
        var added = store.Add(_testRoot, "Task");
        var done = store.Update(_testRoot, added.TaskId, task =>
            task with { Status = WorktreeTaskStatus.Done });

        var renamed = store.Update(_testRoot, added.TaskId, task =>
            task with { Title = "Renamed" });

        Assert.NotNull(renamed);
        Assert.Equal("Renamed", renamed.Title);
        Assert.Equal(done!.DoneUtc, renamed.DoneUtc);
    }

    [Fact]
    public void UpdateReturnsNullForUnknownTaskAndDoesNotWrite()
    {
        var store = new WorktreeTaskStore(new AtomicJsonStore());

        var result = store.Update(_testRoot, "missing", task => task);

        Assert.Null(result);
        Assert.False(File.Exists(WorktreeTaskStore.TasksPath(_testRoot)));
    }

    [Fact]
    public void DeleteRemovesTaskAndReportsUnknownIds()
    {
        var store = new WorktreeTaskStore(new AtomicJsonStore());
        var first = store.Add(_testRoot, "First");
        var second = store.Add(_testRoot, "Second");

        Assert.True(store.Delete(_testRoot, first.TaskId));
        Assert.False(store.Delete(_testRoot, first.TaskId));

        var remaining = Assert.Single(store.Load(_testRoot).Tasks);
        Assert.Equal(second.TaskId, remaining.TaskId);
    }

    [Fact]
    public void ImportsLegacyTasksIdempotentlyAndPreservesFieldsAcrossWorktrees()
    {
        var workbenchRoot = CreateGraphFixture("wb-1", ("wt-1", "one"), ("wt-2", "two"), ("wt-3", "three"));
        var firstRoot = Path.Combine(workbenchRoot, "worktrees", "one");
        var secondRoot = Path.Combine(workbenchRoot, "worktrees", "two");
        var created = DateTimeOffset.UtcNow.AddDays(-2);
        var task = new WorktreeTask("legacy-1", "Imported", "details", WorktreeTaskStatus.Done,
            ["Device/FB"], created, created.AddDays(1));
        Directory.CreateDirectory(firstRoot); Directory.CreateDirectory(secondRoot);
        var jsonStore = new AtomicJsonStore();
        jsonStore.Write(WorktreeTaskStore.TasksPath(firstRoot), new WorktreeTaskList(1, [task]));
        jsonStore.Write(WorktreeTaskStore.TasksPath(secondRoot), new WorktreeTaskList(1, [task with { TaskId = "legacy-2" }]));

        var store = new WorktreeTaskStore(new AtomicJsonStore());
        var imported = Assert.Single(store.Load(firstRoot).Tasks);
        Assert.Equal(task.TaskId, imported.TaskId);
        Assert.Equal(task.Title, imported.Title);
        Assert.Equal(task.Details, imported.Details);
        Assert.Equal(task.Status, imported.Status);
        Assert.Equal(task.ElementRefs, imported.ElementRefs);
        Assert.Equal(task.CreatedUtc, imported.CreatedUtc);
        Assert.Equal(task.DoneUtc, imported.DoneUtc);
        using (var graphStore = new EngineeringGraphStore(workbenchRoot))
        {
            Assert.Equal(2, Convert.ToInt32(Scalar(graphStore.Connection, "SELECT COUNT(*) FROM tasks;")));
        }
        var added = store.Add(firstRoot, "Mirrored");
        Assert.Contains(new AtomicJsonStore().Read<WorktreeTaskList>(WorktreeTaskStore.TasksPath(firstRoot)).Tasks, item => item.TaskId == added.TaskId);
        var changed = store.Update(firstRoot, added.TaskId, item => item with { Title = "Changed" });
        Assert.Equal("Changed", Assert.Single(new AtomicJsonStore().Read<WorktreeTaskList>(WorktreeTaskStore.TasksPath(firstRoot)).Tasks, item => item.TaskId == added.TaskId).Title);
        Assert.True(store.Delete(firstRoot, added.TaskId));
        Assert.DoesNotContain(new AtomicJsonStore().Read<WorktreeTaskList>(WorktreeTaskStore.TasksPath(firstRoot)).Tasks, item => item.TaskId == added.TaskId);
        Assert.Single(store.Load(firstRoot).Tasks);
        Assert.Single(store.Load(secondRoot).Tasks);
        jsonStore.Write(WorktreeTaskStore.TasksPath(secondRoot), new WorktreeTaskList(1, [task]));
        _ = store.Load(secondRoot);
        Assert.Single(store.LastImportDiagnostics);
        Assert.Equal("legacy-1", store.LastImportDiagnostics[0].TaskId);
        Assert.Equal("wt-2", store.LastImportDiagnostics[0].WorktreeId);
        Assert.Equal("Imported", Assert.Single(store.Load(firstRoot).Tasks).Title);
    }

    private string CreateGraphFixture(string workbenchId, params (string Id, string Name)[] worktrees)
    {
        var root = Path.Combine(_testRoot, "graph-workbench");
        Directory.CreateDirectory(Path.Combine(root, "worktrees"));
        var registrations = worktrees.Select(w => new { worktreeId = w.Id, name = w.Name, branch = w.Name, relativePath = w.Name });
        File.WriteAllText(Path.Combine(root, "workbench.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "1.2", workbenchId, name = "Test", createdAt = DateTimeOffset.UtcNow.ToString("O"), rootPath = root,
            repositoryPath = Path.Combine(root, "repo"), engineeringProjectId = (string?)null, sourceProjectPath = (string?)null,
            worktrees = registrations
        }));
        foreach (var worktree in worktrees)
        {
            var path = Path.Combine(root, "worktrees", worktree.Name);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "worktree.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = "1.2", worktreeId = worktree.Id, workbenchId, name = worktree.Name, branch = worktree.Name,
                createdAt = DateTimeOffset.UtcNow.ToString("O"), baseCommit = (string?)null, engineeringProjectId = (string?)null,
                sourceProjectPath = (string?)null, deviceIds = Array.Empty<string>(), lastReconciliationCommit = (string?)null
            }));
        }
        return root;
    }

    private static long Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
