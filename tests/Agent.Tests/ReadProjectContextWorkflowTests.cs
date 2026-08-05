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
        Directory.CreateDirectory(context.SourceRoot);
        var sentinel = Path.Combine(context.SourceRoot, "sentinel.xml");
        File.WriteAllText(sentinel, "unchanged");
        var engineering = new ManifestExportCaller()
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
        Assert.StartsWith(context.DeviceRoot, Property<string>(args, "outputDir"));
        Assert.Equal("PLC_1", Property<string>(args, "plcName"));
        Assert.True(File.Exists(Path.Combine(context.StagingRoot, "metadata.json")));
        Assert.Equal("unchanged", File.ReadAllText(sentinel));
        Assert.Equal(context.DeviceId, result.DeviceId);
        Assert.Equal(
            context.SourceRoot,
            result.GetType().GetProperty("SourceRoot")?.GetValue(result));
        Assert.Null(result.GetType().GetProperty("ExportRoot"));
        Assert.Null(result.GetType().GetProperty("ModifiedSourceRoot"));
        Assert.Equal(context.KnowledgeDbPath, result.DbPath);
    }

    [Fact]
    public async Task MissingKnowledgeIsNotMutatedByStagingWorkflow()
    {
        var context = Context();
        Directory.CreateDirectory(context.SourceRoot);
        File.WriteAllText(Path.Combine(context.SourceRoot, "metadata.json"), "{}");
        var engineering = new ManifestExportCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "Line" })
            .Respond("rebuild_export", new[] { new SyncResult { PlcName = "PLC_1" } });
        var knowledge = new FakeToolCaller();
        var workflow = new ReadProjectContextWorkflow(
            engineering,
            knowledge,
            fileExists: _ => false);

        await workflow.RunAsync(context, "PLC_1");

        Assert.Empty(knowledge.Calls);
    }

    [Fact]
    public async Task NoSourceDiffWithMissingDatabaseIsNotUpToDate()
    {
        var context = Context();
        WriteDeviceMetadata(context, stale: false);
        var engineering = new ManifestExportCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "Line" })
            .Respond("rebuild_export", new[]
            {
                new SyncResult { PlcName = "PLC_1", BaselineExisted = true },
            });
        var workflow = new ReadProjectContextWorkflow(
            engineering,
            new FakeToolCaller(),
            fileExists: _ => false);

        var result = await workflow.RunAsync(context, "PLC_1");

        Assert.False(result.ApprovalRequired);
        Assert.False(result.UpToDate);
    }

    [Fact]
    public async Task NoSourceDiffWithStaleKnowledgeIsNotUpToDate()
    {
        var context = Context();
        WriteDeviceMetadata(context, stale: true);
        var engineering = new ManifestExportCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "Line" })
            .Respond("rebuild_export", new[]
            {
                new SyncResult { PlcName = "PLC_1", BaselineExisted = true },
            });
        var workflow = new ReadProjectContextWorkflow(
            engineering,
            new FakeToolCaller(),
            fileExists: _ => true);

        var result = await workflow.RunAsync(context, "PLC_1");

        Assert.False(result.ApprovalRequired);
        Assert.False(result.UpToDate);
    }

    [Fact]
    public async Task NoSourceDiffWithCurrentKnowledgeIsUpToDate()
    {
        var context = Context();
        WriteDeviceMetadata(context, stale: false);
        var engineering = new ManifestExportCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "Line" })
            .Respond("rebuild_export", new[]
            {
                new SyncResult { PlcName = "PLC_1", BaselineExisted = true },
            });
        var workflow = new ReadProjectContextWorkflow(
            engineering,
            new FakeToolCaller(),
            fileExists: _ => true);

        var result = await workflow.RunAsync(context, "PLC_1");

        Assert.True(result.UpToDate);
    }

    [Fact]
    public async Task FirstStagedExportDoesNotIngestBeforeBaselineApproval()
    {
        var context = Context();
        var engineering = new ManifestExportCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "Line" })
            .Respond("rebuild_export", new[]
            {
                new SyncResult
                {
                    PlcName = "PLC_1",
                    BaselineExisted = false,
                    Added = new[] { new SyncChange { Name = "A" } },
                },
            });
        var knowledge = new FakeToolCaller();
        var workflow = new ReadProjectContextWorkflow(
            engineering,
            knowledge,
            fileExists: _ => false);

        var result = await workflow.RunAsync(context, "PLC_1");

        Assert.True(result.ApprovalRequired);
        Assert.Empty(knowledge.Calls);
    }

    [Fact]
    public async Task PartialExportThroughWorkflowPreservesPreviousStaging()
    {
        var context = Context();
        Directory.CreateDirectory(context.StagingRoot);
        File.WriteAllText(Path.Combine(context.StagingRoot, "sentinel.txt"), "previous");
        var engineering = new ManifestExportCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "Line" })
            .Respond("rebuild_export", new[]
            {
                new SyncResult
                {
                    PlcName = "PLC_1",
                    Failed = new[] { new SyncChange { Name = "A" } },
                },
            });
        var workflow = new ReadProjectContextWorkflow(engineering, new FakeToolCaller());

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(
            () => workflow.RunAsync(context, "PLC_1"));

        Assert.Equal("DEVICE_EXPORT_INCOMPLETE", error.Code);
        Assert.Equal(
            "previous",
            File.ReadAllText(Path.Combine(context.StagingRoot, "sentinel.txt")));
    }

    [Fact]
    public async Task SeparateWorkflowsSerializeStagingForSameDevice()
    {
        var context = Context();
        var caller = new ConcurrentExportCaller();
        var first = new ReadProjectContextWorkflow(caller, new FakeToolCaller());
        var second = new ReadProjectContextWorkflow(caller, new FakeToolCaller());

        await Task.WhenAll(
            first.RunAsync(context, "PLC_1"),
            second.RunAsync(context, "PLC_1"));

        Assert.Equal(1, caller.MaxConcurrentExports);
    }

    [Fact]
    public async Task ExistingKnowledgeIsNotRebuiltFromUnapprovedStage()
    {
        var context = Context();
        var engineering = new ManifestExportCaller()
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
        var engineering = new ManifestExportCaller()
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

    private static void WriteDeviceMetadata(DeviceContext context, bool stale)
    {
        new AtomicJsonStore().Write(
            Path.Combine(context.DeviceRoot, "device.json"),
            new DeviceMetadata(
                WorkbenchSchema.CurrentVersion,
                context.DeviceId,
                context.WorktreeId,
                "PLC_1",
                "PLC_1",
                null,
                null,
                null,
                new KnowledgeState(
                    stale,
                    new Dictionary<string, string>(),
                    null),
                Array.Empty<DeviceImportRecord>()));
    }

    private static T Property<T>(object value, string name) =>
        (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    private sealed class ManifestExportCaller : FakeToolCaller
    {
        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            if (tool == "rebuild_export")
            {
                var output = Property<string>(args, "outputDir");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "metadata.json"), "{}");
            }

            return base.CallAsync<T>(tool, args, cancellationToken);
        }
    }

    private sealed class ConcurrentExportCaller : FakeToolCaller
    {
        private int activeExports;
        private int maxConcurrentExports;

        public int MaxConcurrentExports => maxConcurrentExports;

        public override async Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            if (tool == "get_project_info")
            {
                return (T)(object)new ProjectInfo { Name = "Line" };
            }

            var active = Interlocked.Increment(ref activeExports);
            UpdateMaximum(active);
            try
            {
                var output = Property<string>(args, "outputDir");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "metadata.json"), "{}");
                await Task.Delay(75, cancellationToken);
                return (T)(object)new[]
                {
                    new SyncResult { PlcName = "PLC_1", BaselineExisted = true },
                };
            }
            finally
            {
                Interlocked.Decrement(ref activeExports);
            }
        }

        private void UpdateMaximum(int value)
        {
            var observed = Volatile.Read(ref maxConcurrentExports);
            while (value > observed)
            {
                var previous = Interlocked.CompareExchange(
                    ref maxConcurrentExports,
                    value,
                    observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }
}
