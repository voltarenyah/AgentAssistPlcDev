// Tool tests for the stage-5 query helpers get_block / get_network / search (buildnote/plan/mcp-knowledge.md §6).
// Runs against a temp SQLite DB ingested from the committed fixtures, via the real MCP tool surface.
using System;
using System.IO;
using System.Linq;
using Mcp.Knowledge.Tools;
using Xunit;

namespace Mcp.Knowledge.Tests;

public sealed class QueryHelperToolTests
{
    [Fact]
    public void GetBlockReturnsNetworksWithLogicStatements()
    {
        using var db = new FixtureDb();
        var tools = new KnowledgeTools();

        var result = ToolResults.OkJson(tools.GetBlock(db.Path, "Main"));

        var block = result.GetProperty("block");
        Assert.Equal("block:Main", block.GetProperty("id").GetString());
        Assert.Equal("OB", block.GetProperty("kind").GetString());
        Assert.Equal("Main [OB1].xml", block.GetProperty("sourceFile").GetString());

        var networks = result.GetProperty("networks").EnumerateArray().ToArray();
        Assert.Equal(2, networks.Length);
        Assert.Equal(1, networks[0].GetProperty("index").GetInt32());
        Assert.Equal("3", networks[0].GetProperty("compileUnitId").GetString());
        Assert.Contains("FC_LAD_SimulateCylinder_Call(", networks[0].GetProperty("logicStatements").GetString());
        // Empty network: the logicStatements property is omitted.
        Assert.Equal(2, networks[1].GetProperty("index").GetInt32());
        Assert.False(networks[1].TryGetProperty("logicStatements", out _));
    }

    [Fact]
    public void GetBlockRejectsUnknownBlock()
    {
        using var db = new FixtureDb();
        var tools = new KnowledgeTools();

        var error = ToolResults.ErrorJson(tools.GetBlock(db.Path, "DoesNotExist"));

        Assert.Equal("BLOCK_NOT_FOUND", error.GetProperty("code").GetString());
    }

    [Fact]
    public void GetNetworkReturnsLogicAccessesAndCalls()
    {
        using var db = new FixtureDb();
        var tools = new KnowledgeTools();

        var result = ToolResults.OkJson(tools.GetNetwork(db.Path, "Main", 1));

        var network = result.GetProperty("network");
        Assert.Equal("network:Main:1", network.GetProperty("id").GetString());
        Assert.Equal("3", network.GetProperty("compileUnitId").GetString());
        Assert.Contains("FC_LAD_SimulateCylinder_Call(", network.GetProperty("logicStatements").GetString());

        var reads = result.GetProperty("reads").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("Btn_ForwardCommand", reads);
        Assert.Equal(reads.Distinct().Count(), reads.Length);

        var writes = result.GetProperty("writes").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("CylinderGoForwardPos", writes);

        var calls = result.GetProperty("calls").EnumerateArray().ToArray();
        var call = Assert.Single(calls);
        Assert.Equal("FC_LAD_SimulateCylinder_Call", call.GetProperty("name").GetString());
        Assert.Equal("FC", call.GetProperty("kind").GetString());
    }

    [Fact]
    public void GetNetworkRejectsUnknownIndex()
    {
        using var db = new FixtureDb();
        var tools = new KnowledgeTools();

        var error = ToolResults.ErrorJson(tools.GetNetwork(db.Path, "Main", 42));

        Assert.Equal("NETWORK_NOT_FOUND", error.GetProperty("code").GetString());
        Assert.Contains("42", error.GetProperty("message").GetString());
    }

    [Fact]
    public void SearchMatchesNodeNames()
    {
        using var db = new FixtureDb();
        var tools = new KnowledgeTools();

        var result = ToolResults.OkJson(tools.Search(db.Path, "SimulateCylinder_Call", null, null));

        var matches = result.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Contains(matches, match =>
            match.GetProperty("id").GetString() == "block:FC_LAD_SimulateCylinder_Call" &&
            match.GetProperty("matchedIn").GetString() == "name");
        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void SearchMatchesLogicStatementsWithKindFilter()
    {
        using var db = new FixtureDb();
        var tools = new KnowledgeTools();

        var result = ToolResults.OkJson(tools.Search(db.Path, "Btn_ForwardCommand", "Network", null));

        var matches = result.GetProperty("matches").EnumerateArray().ToArray();
        Assert.NotEmpty(matches);
        Assert.All(matches, match =>
        {
            Assert.Equal("Network", match.GetProperty("kind").GetString());
            Assert.Equal("logicStatements", match.GetProperty("matchedIn").GetString());
        });
        Assert.Contains(matches, match => match.GetProperty("id").GetString() == "network:Main:1");
    }

    [Fact]
    public void SearchRejectsEmptyText()
    {
        using var db = new FixtureDb();
        var tools = new KnowledgeTools();

        var error = ToolResults.ErrorJson(tools.Search(db.Path, "  ", null, null));

        Assert.Equal("SEARCH_TEXT_REQUIRED", error.GetProperty("code").GetString());
    }

    [Fact]
    public void HelpersRejectMissingDb()
    {
        var tools = new KnowledgeTools();
        var missing = Path.Combine(Path.GetTempPath(), "Mcp.Knowledge.Tests", "does-not-exist.db");

        var error = ToolResults.ErrorJson(tools.GetBlock(missing, "Main"));

        Assert.Equal("DB_NOT_FOUND", error.GetProperty("code").GetString());
    }

    private sealed class FixtureDb : IDisposable
    {
        private readonly TempExportTree tree;

        public FixtureDb()
        {
            tree = new TempExportTree();
            tree.AddFixture(FixtureFiles.MainObPath, "Main [OB1].xml");
            tree.AddFixture(FixtureFiles.SimulateCylinderFcPath, "FC_LAD_SimulateCylinder_Call [FC1].xml");
            tree.AddFixture(FixtureFiles.GlobalDataDbPath, "GlobalData [DB1].xml");
            tree.AddFixture(FixtureFiles.MotorFbInstanceDbPath, "MotorFbInstance [DB2].xml");
            Path = System.IO.Path.Combine(tree.Root, "plc-knowledge.db");
            ToolResults.OkJson(new KnowledgeTools().IngestSource(tree.Root, Path));
        }

        public string Path { get; }

        public void Dispose() => tree.Dispose();
    }
}
