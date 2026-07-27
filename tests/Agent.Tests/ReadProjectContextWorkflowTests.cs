using Agent.Mcp;
using Agent.Workbench;
using Agent.Workflows;
using Contracts.Engineering;
using Contracts.Knowledge;
using Xunit;

namespace Agent.Tests;

public sealed class ReadProjectContextWorkflowTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"context-workflow-{Guid.NewGuid():N}");

    [Fact]
    public async Task StagesSelectedDeviceWithoutTouchingTrackedBaseline()
    {
        var context = Context();
        Directory.CreateDirectory(context.ExportedSourceRoot);
        var sentinel = Path.Combine(context.ExportedSourceRoot, "sentinel.xml");
        File.WriteAllText(sentinel, "unchanged");
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "Line", PlcDevices = new[] { "PLC_1" } })
            .Respond("rebuild_export", new[]
            {
                new SyncResult { PlcName = "PLC_1", ExportRoot = context.StagingRoot },
            });
        var workflow = new ReadProjectContextWorkflow(
            engineering,
            new FakeToolCaller(),
            fileExists: _ => true);

        var result = await workflow.RunAsync(context, "PLC_1");

        Assert.Equal(new[] { "get_project_info", "rebuild_export" }, engineering.Calls);
        var args = engineering.CallArgs["rebuild_export"].Single();
        Assert.Equal(context.StagingRoot, Property<string>(args, "outputDir"));
        Assert.Equal("PLC_1", Property<string>(args, "plcName"));
        Assert.Equal("unchanged", File.ReadAllText(sentinel));
        Assert.Equal(context.DeviceId, result.DeviceId);
        Assert.Equal(context.KnowledgeDbPath, result.DbPath);
    }

    [Fact]
    public async Task MissingKnowledgeBuildsFromBaselinePlusOverlayNeverStaging()
    {
        var context = Context();
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "Line" })
            .Respond("rebuild_export", new[] { new SyncResult { PlcName = "PLC_1" } });
        var knowledge = new FakeToolCaller()
            .Respond("ingest_source", new IngestResult { DbPath = context.KnowledgeDbPath });
        var workflow = new ReadProjectContextWorkflow(
            engineering,
            knowledge,
            fileExists: _ => false);

        await workflow.RunAsync(context, "PLC_1");

        var args = knowledge.CallArgs["ingest_source"].Single();
        Assert.Equal(context.ExportedSourceRoot, Property<string>(args, "exportedSourceRoot"));
        Assert.Equal(context.ModifiedSourceRoot, Property<string>(args, "modifiedSourceRoot"));
        Assert.Equal(context.KnowledgeDbPath, Property<string>(args, "dbPath"));
    }

    [Fact]
    public async Task ExistingKnowledgeIsNotRebuiltFromUnapprovedStage()
    {
        var context = Context();
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "Line" })
            .Respond("rebuild_export", new[]
            {
                new SyncResult
                {
                    PlcName = "PLC_1",
                    Status = "updated",
                    Added = new[] { new SyncChange { Name = "A" } },
                },
            });
        var knowledge = new FakeToolCaller();
        var workflow = new ReadProjectContextWorkflow(
            engineering,
            knowledge,
            fileExists: _ => true);

        var result = await workflow.RunAsync(context, "PLC_1");

        Assert.Empty(knowledge.Calls);
        Assert.True(result.ApprovalRequired);
        Assert.False(result.UpToDate);
    }

    [Fact]
    public async Task CancellationAfterProjectInfoStopsBeforeStage()
    {
        var context = Context();
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "Line" });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var workflow = new ReadProjectContextWorkflow(engineering, new FakeToolCaller());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => workflow.RunAsync(context, "PLC_1", cancellation.Token));

        Assert.Equal(new[] { "get_project_info" }, engineering.Calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private DeviceContext Context() =>
        WorkbenchPaths.ResolveDevice(
            "wb-1", Path.Combine(root, Guid.NewGuid().ToString("N")),
            "wt-1", "master", "dev-1", "PLC_1");

    private static T Property<T>(object value, string name) =>
        (T)value.GetType().GetProperty(name)!.GetValue(value)!;
}
