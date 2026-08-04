using Agent.Workbench;
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

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }
}
