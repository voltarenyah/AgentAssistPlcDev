using System.Text.Json;
using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class WorkbenchWritePolicyTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "workbench-write-policy-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void NormalEditIsAllowedOnMaster()
    {
        var policy = PolicyFor("master");

        // MASTER_EDIT_NOT_ALLOWED is disabled: direct master edits are allowed by policy.
        policy.RequireFeatureEdit(ContextFor("master"));
    }

    [Fact]
    public void NormalEditIsAllowedOnFeature()
    {
        var policy = PolicyFor("feature-a");

        policy.RequireFeatureEdit(ContextFor("feature-a"));
    }

    [Fact]
    public void MissingWorktreeMetadataFailsClosed()
    {
        var context = ContextFor("unknown");
        var policy = new WorkbenchWritePolicy(new AtomicJsonStore());

        var error = Assert.Throws<WorkbenchLifecycleException>(() =>
            policy.RequireFeatureEdit(context));

        Assert.Equal("WORKTREE_METADATA_REQUIRED", error.Code);
    }

    private WorkbenchWritePolicy PolicyFor(string branch)
    {
        var context = ContextFor(branch);
        var worktree = new WorktreeMetadata(
            WorkbenchSchema.CurrentVersion,
            context.WorktreeId,
            context.WorkbenchId,
            branch,
            branch,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            null,
            null,
            [],
            null);
        new AtomicJsonStore().Write(
            Path.Combine(context.WorktreeRoot, "worktree.json"),
            worktree);

        return new WorkbenchWritePolicy(new AtomicJsonStore());
    }

    private DeviceContext ContextFor(string branch)
    {
        var context = WorkbenchPaths.ResolveDevice(
            "wb-1", root, "wt-1", branch, "dev-1", "PLC_1");
        Directory.CreateDirectory(context.WorktreeRoot);
        return context;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
