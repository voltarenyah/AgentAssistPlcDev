using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class WorkbenchRuntimeStateTests
{
    [Fact]
    public void NewWorkbenchStartsAtRevisionZeroWithNoFocusOrOperation()
    {
        var coordinator = new WorkbenchRuntimeStateCoordinator();

        var snapshot = coordinator.GetSnapshot("wb-1");

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal("wb-1", snapshot.WorkbenchId);
        Assert.Equal(0, snapshot.WorkbenchRevision);
        Assert.Null(snapshot.Focus.WorktreeId);
        Assert.Null(snapshot.Focus.DeviceId);
        Assert.Empty(snapshot.Worktrees);
        Assert.Equal(RuntimeOperationStatus.Idle, snapshot.Operation.Status);
    }

    [Fact]
    public void SetFocusPublishesNewSnapshotAndIncrementsRevision()
    {
        var coordinator = new WorkbenchRuntimeStateCoordinator();
        var published = new List<WorkbenchRuntimeSnapshot>();
        coordinator.StateChanged += published.Add;

        var snapshot = coordinator.SetFocus("wb-1", "wt-1", "plc-1");

        Assert.Equal(1, snapshot.WorkbenchRevision);
        Assert.Equal("wt-1", snapshot.Focus.WorktreeId);
        Assert.Equal("plc-1", snapshot.Focus.DeviceId);
        Assert.Single(published);
        Assert.Same(snapshot, published[0]);
    }

    [Fact]
    public void StaleExpectedRevisionIsRejectedBeforeChangingState()
    {
        var coordinator = new WorkbenchRuntimeStateCoordinator();
        coordinator.SetFocus("wb-1", "wt-1", null);

        var error = Assert.Throws<RuntimeStateConflictException>(() =>
            coordinator.SetFocus("wb-1", "wt-2", null, expectedRevision: 0));

        Assert.Equal("CONTEXT_STALE", error.Code);
        Assert.Equal(0, error.ExpectedRevision);
        Assert.Equal(1, error.ActualRevision);
        Assert.Equal("wt-1", coordinator.GetSnapshot("wb-1").Focus.WorktreeId);
    }

    [Fact]
    public void RefreshReplacesWorktreeSummariesAndEnablesReadActions()
    {
        var coordinator = new WorkbenchRuntimeStateCoordinator();
        var worktrees = new[]
        {
            new WorktreeRuntimeSummary(
                "wt-1", "Feature A", "feature/a", "dirty", "abc123", 2,
                25, 31, "requires_scan", Array.Empty<DeviceRuntimeSummary>()),
        };

        var snapshot = coordinator.Refresh("wb-1", worktrees);

        var worktree = Assert.Single(snapshot.Worktrees);
        Assert.Equal("Feature A", worktree.Name);
        Assert.Equal("feature/a", worktree.Branch);
        Assert.Equal("dirty", worktree.GitStatus);
        Assert.Equal(2, worktree.TodoCount);
        Assert.Contains(snapshot.AvailableActions, action =>
            action.Id == "read_worktree_todos" && action.Enabled);
        Assert.Contains(snapshot.AvailableActions, action =>
            action.Id == "read_commit_history" && action.Enabled);
        Assert.Contains(snapshot.AvailableActions, action =>
            action.Id == "create_worktree" && action.Enabled && action.RequiresApproval);
    }

    [Fact]
    public void RunningOperationDisablesMutatingCapabilityButKeepsReadActions()
    {
        var coordinator = new WorkbenchRuntimeStateCoordinator();
        coordinator.Refresh("wb-1", Array.Empty<WorktreeRuntimeSummary>());

        var running = coordinator.StartOperation("wb-1", "op-1", "create-worktree");

        Assert.Equal(RuntimeOperationStatus.Running, running.Operation.Status);
        Assert.Contains(running.AvailableActions, action =>
            action.Id == "create_worktree" && !action.Enabled);
        Assert.Contains(running.AvailableActions, action =>
            action.Id == "read_svn_state" && action.Enabled);
    }

    [Fact]
    public void SameCommandRequestIdDoesNotApplyTheTransitionTwice()
    {
        var coordinator = new WorkbenchRuntimeStateCoordinator();
        var command = new SetFocusCommand("wb-1", "request-1", null, "ui", "wt-1", null);

        var first = coordinator.Execute(command);
        var second = coordinator.Execute(command);

        Assert.Same(first, second);
        Assert.Equal(1, second.WorkbenchRevision);
    }

    [Fact]
    public void ObservationsUpdateOnlyTheTargetWorktree()
    {
        var coordinator = new WorkbenchRuntimeStateCoordinator();
        coordinator.Refresh("wb-1", new[]
        {
            new WorktreeRuntimeSummary(
                "wt-1", "Feature A", "feature/a", "clean", "abc", 0,
                null, null, "valid", Array.Empty<DeviceRuntimeSummary>()),
            new WorktreeRuntimeSummary(
                "wt-2", "Feature B", "feature/b", "dirty", "def", 3,
                null, null, "unknown", Array.Empty<DeviceRuntimeSummary>()),
        });

        var snapshot = coordinator.ObserveTodos("wb-1", "wt-1", 4);

        Assert.Equal(4, snapshot.Worktrees.Single(worktree => worktree.WorktreeId == "wt-1").TodoCount);
        Assert.Equal(3, snapshot.Worktrees.Single(worktree => worktree.WorktreeId == "wt-2").TodoCount);
    }
}
