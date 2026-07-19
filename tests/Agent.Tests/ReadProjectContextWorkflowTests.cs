using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using App.Mcp;
using App.Workflows;
using Contracts.Engineering;
using Contracts.Knowledge;
using Xunit;

namespace App.Tests;

public sealed class ReadProjectContextWorkflowTests
{
    [Fact]
    public async Task RunsExportChainInOrderAndMapsResult()
    {
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "TestPLC" })
            .Respond("export_all_blocks", new[]
            {
                new ExportResult { BlockName = "A", Success = true },
                new ExportResult { BlockName = "B", Success = false, Error = "skipped" },
                new ExportResult { BlockName = "C", Success = true },
            })
            .Respond("export_tag_tables", new[] { new ExportResult { BlockName = "Tags", Success = true } })
            .Respond("export_udts", new ExportResult[] { });
        var knowledge = new FakeToolCaller()
            .Respond("ingest_source", new IngestResult { DbPath = "x.db", Nodes = 10, Edges = 20 });
        var progress = new List<string>();
        var workflow = new ReadProjectContextWorkflow(engineering, knowledge, new Progress<string>(progress.Add));

        var result = await workflow.RunAsync();

        Assert.Equal(
            new[] { "get_project_info", "export_all_blocks", "export_tag_tables", "export_udts" },
            engineering.Calls.ToArray());
        Assert.Equal(new[] { "ingest_source" }, knowledge.Calls.ToArray());

        // Same export root everywhere, derived from the project name.
        foreach (var call in new[] { "export_all_blocks", "export_tag_tables", "export_udts" })
        {
            var args = engineering.CallArgs[call][0];
            var outputDir = (string)args.GetType().GetProperty("outputDir")!.GetValue(args)!;
            Assert.EndsWith("TestPLC", outputDir);
        }

        var ingestArgs = knowledge.CallArgs["ingest_source"][0];
        var exportRoot = (string)ingestArgs.GetType().GetProperty("exportRoot")!.GetValue(ingestArgs)!;
        Assert.EndsWith("TestPLC", exportRoot);

        // Only successful exports count.
        Assert.Equal("TestPLC", result.ProjectName);
        Assert.Equal(2, result.BlocksExported);
        Assert.Equal(1, result.TagTablesExported);
        Assert.Equal(0, result.UdtsExported);
        Assert.Equal("x.db", result.DbPath);
        Assert.Equal(10, result.Ingest.Nodes);
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task ProjectInfoErrorAbortsBeforeAnyExport()
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
    public async Task ExportErrorAbortsBeforeIngest()
    {
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "TestPLC" })
            .Fail("export_all_blocks", "EXPORT_FAILED", "Block export failed.");
        var knowledge = new FakeToolCaller();
        var workflow = new ReadProjectContextWorkflow(engineering, knowledge);

        var error = await Assert.ThrowsAsync<ToolCallException>(() => workflow.RunAsync());

        Assert.Equal("EXPORT_FAILED", error.Code);
        Assert.Empty(knowledge.Calls);
    }

    [Fact]
    public async Task ZeroBlocksExportedAbortsBeforeIngest()
    {
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Name = "TestPLC" })
            .Respond("export_all_blocks", new ExportResult[] { })
            .Respond("export_tag_tables", new ExportResult[] { })
            .Respond("export_udts", new ExportResult[] { });
        var knowledge = new FakeToolCaller();
        var workflow = new ReadProjectContextWorkflow(engineering, knowledge);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync());

        Assert.Contains("0 blocks", error.Message);
        Assert.Empty(knowledge.Calls);
    }

    [Fact]
    public async Task CancelledTokenStopsChainBeforeExports()
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
}
