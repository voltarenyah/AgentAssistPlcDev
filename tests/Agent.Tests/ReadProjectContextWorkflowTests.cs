using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Agent.Mcp;
using Agent.Workflows;
using Contracts.Engineering;
using Contracts.Knowledge;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// The confirmed incremental sync (buildnote/plan/export-sync.md §UI): sync_export first, then
/// ingest_source only when content changed or the knowledge db is missing.
/// </summary>
public sealed class ReadProjectContextWorkflowTests
{
    [Fact]
    public async Task RunsSyncThenIngest_InOrder_WhenContentChanged()
    {
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "TestPLC" })
            .Respond("sync_export", new[]
            {
                Plc(status: "updated", changed: new[] { Change("FB_Motor") }),
            });
        var knowledge = new FakeToolCaller()
            .Respond("ingest_source", new IngestResult { DbPath = "x.db", Nodes = 10, Edges = 20 });
        var progress = new List<string>();
        var workflow = new ReadProjectContextWorkflow(
            engineering, knowledge, new Progress<string>(progress.Add), fileExists: _ => true);

        var result = await workflow.RunAsync();

        Assert.Equal(new[] { "get_project_info", "sync_export" }, engineering.Calls.ToArray());
        Assert.Equal(new[] { "ingest_source" }, knowledge.Calls.ToArray());

        // Same export root everywhere, derived from the project name.
        var syncArgs = engineering.CallArgs["sync_export"][0];
        var outputDir = (string)syncArgs.GetType().GetProperty("outputDir")!.GetValue(syncArgs)!;
        Assert.EndsWith("TestPLC", outputDir);
        var ingestArgs = knowledge.CallArgs["ingest_source"][0];
        var exportRoot = (string)ingestArgs.GetType().GetProperty("exportRoot")!.GetValue(ingestArgs)!;
        Assert.EndsWith("TestPLC", exportRoot);

        Assert.Equal("TestPLC", result.ProjectName);
        Assert.False(result.UpToDate);
        Assert.Equal("x.db", result.DbPath);
        Assert.NotNull(result.Ingest);
        Assert.Equal(10, result.Ingest!.Nodes);
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task UnchangedAndDbExists_SkipsIngest()
    {
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "TestPLC" })
            .Respond("sync_export", new[] { Plc() });
        var knowledge = new FakeToolCaller();
        var workflow = new ReadProjectContextWorkflow(engineering, knowledge, fileExists: _ => true);

        var result = await workflow.RunAsync();

        Assert.Empty(knowledge.Calls);
        Assert.True(result.UpToDate);
        Assert.Null(result.Ingest);
        Assert.EndsWith("plc-knowledge.db", result.DbPath);
    }

    [Fact]
    public async Task UnchangedButDbMissing_RunsIngest()
    {
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "TestPLC" })
            .Respond("sync_export", new[] { Plc() });
        var knowledge = new FakeToolCaller()
            .Respond("ingest_source", new IngestResult { DbPath = "x.db", Nodes = 10, Edges = 20 });
        var workflow = new ReadProjectContextWorkflow(engineering, knowledge, fileExists: _ => false);

        var result = await workflow.RunAsync();

        Assert.Equal(new[] { "ingest_source" }, knowledge.Calls.ToArray());
        Assert.False(result.UpToDate);
        Assert.NotNull(result.Ingest);
    }

    [Fact]
    public async Task ContentChange_RunsIngest_EvenWhenDbExists()
    {
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "TestPLC" })
            .Respond("sync_export", new[] { Plc(status: "updated", removed: new[] { Change("FB_Old") }) });
        var knowledge = new FakeToolCaller()
            .Respond("ingest_source", new IngestResult { DbPath = "x.db" });
        var workflow = new ReadProjectContextWorkflow(engineering, knowledge, fileExists: _ => true);

        await workflow.RunAsync();

        Assert.Equal(new[] { "ingest_source" }, knowledge.Calls.ToArray());
    }

    [Fact]
    public async Task ProjectInfoErrorAbortsBeforeAnySync()
    {
        var engineering = new FakeToolCaller()
            .Fail("get_project_info", "NOT_CONNECTED", "No TIA session is connected.");
        var knowledge = new FakeToolCaller();
        var workflow = new ReadProjectContextWorkflow(engineering, knowledge);

        var error = await Assert.ThrowsAsync<ToolCallException>(() => workflow.RunAsync());

        Assert.Equal("NOT_CONNECTED", error.Code);
        Assert.Equal(new[] { "get_project_info" }, engineering.Calls.ToArray());
        Assert.Empty(knowledge.Calls);
    }

    [Fact]
    public async Task SyncErrorAbortsBeforeIngest()
    {
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "TestPLC" })
            .Fail("sync_export", "SANDBOX_TOOL_DENIED", "Tool disabled.");
        var knowledge = new FakeToolCaller();
        var workflow = new ReadProjectContextWorkflow(engineering, knowledge);

        var error = await Assert.ThrowsAsync<ToolCallException>(() => workflow.RunAsync());

        Assert.Equal("SANDBOX_TOOL_DENIED", error.Code);
        Assert.Empty(knowledge.Calls);
    }

    [Fact]
    public async Task NoBaselineWithZeroAdded_Throws()
    {
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "TestPLC" })
            .Respond("sync_export", new[] { Plc(baselineExisted: false, status: "updated") });
        var knowledge = new FakeToolCaller();
        var workflow = new ReadProjectContextWorkflow(engineering, knowledge);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync());

        Assert.Contains("0 components", error.Message);
        Assert.Empty(knowledge.Calls);
    }

    [Fact]
    public async Task CancelledTokenStopsChainBeforeSync()
    {
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "TestPLC" });
        var knowledge = new FakeToolCaller();
        var workflow = new ReadProjectContextWorkflow(engineering, knowledge);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => workflow.RunAsync(cancellation.Token));

        Assert.Equal(new[] { "get_project_info" }, engineering.Calls.ToArray());
        Assert.Empty(knowledge.Calls);
    }

    private static SyncResult Plc(
        bool baselineExisted = true,
        string status = "unchanged",
        SyncChange[]? added = null,
        SyncChange[]? changed = null,
        SyncChange[]? removed = null,
        SyncChange[]? failed = null) => new()
        {
            PlcName = "PLC_1",
            ExportRoot = "root",
            Status = status,
            BaselineExisted = baselineExisted,
            Added = added ?? Array.Empty<SyncChange>(),
            Changed = changed ?? Array.Empty<SyncChange>(),
            Removed = removed ?? Array.Empty<SyncChange>(),
            Failed = failed ?? Array.Empty<SyncChange>(),
        };

    private static SyncChange Change(string name) => new() { Name = name, Category = "FB" };
}
