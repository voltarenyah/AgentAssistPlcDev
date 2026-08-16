// Tool tests for the stage-5 query helpers get_block / get_single_network / get_network_logic / get_all_networks / search (buildnote/plan/mcp-knowledge.md §6).
// Runs against a temp SQLite DB ingested from the committed fixtures, via the real MCP tool surface.
using System;
using System.IO;
using System.Linq;
using Mcp.Knowledge.Tools;
using Microsoft.Data.Sqlite;
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
    public void GetSingleNetworkReturnsOnlyRequestedNetwork()
    {
        using var db = new FixtureDb();
        var tools = new KnowledgeTools();

        var result = ToolResults.OkJson(tools.GetSingleNetwork(
            db.Path,
            "Main",
            1,
            new[] { "logic", "access", "calls" }));

        Assert.Equal("network:Main:1", result.GetProperty("network").GetProperty("id").GetString());
        Assert.Contains("FC_LAD_SimulateCylinder_Call(", result.GetProperty("network").GetProperty("logicStatements").GetString());
        Assert.DoesNotContain(result.GetProperty("network").GetProperty("id").GetString(), new[] { "network:Main:2" });
        Assert.Contains("Btn_ForwardCommand", result.GetProperty("reads").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void GetAllNetworksDefaultsToSummaries()
    {
        using var db = new FixtureDb();
        var tools = new KnowledgeTools();

        var result = ToolResults.OkJson(tools.GetAllNetworks(db.Path, "Main"));
        var networks = result.GetProperty("networks").EnumerateArray().ToArray();

        Assert.Equal(2, networks.Length);
        Assert.False(networks[0].TryGetProperty("logicStatements", out _));
        Assert.Equal(2, result.GetProperty("meta").GetProperty("returned").GetInt32());
    }

    [Fact]
    public void GetAllNetworksBoundsRequestedLogic()
    {
        using var db = new FixtureDb();
        var tools = new KnowledgeTools();

        var result = ToolResults.OkJson(tools.GetAllNetworks(
            db.Path,
            "Main",
            new[] { "logic" },
            40));
        var networks = result.GetProperty("networks").EnumerateArray().ToArray();

        Assert.All(networks, network =>
        {
            if (network.TryGetProperty("logicStatements", out var logic))
            {
                Assert.True(logic.GetString()!.Length <= 40);
            }
        });
    }

    [Fact]
    public void GetNetworkLogicReturnsReassemblableChunksForLongLogic()
    {
        using var db = new FixtureDb();
        var logic = string.Concat(Enumerable.Range(0, 80).Select(index => $"statement_{index:D2};"));
        db.ReplaceNetworkLogic(logic);
        var tools = new KnowledgeTools();

        var chunks = new System.Collections.Generic.List<System.Text.Json.JsonElement>();
        var offset = 0;
        do
        {
            var chunk = ToolResults.OkJson(tools.GetNetworkLogic(db.Path, "Main", 1, offset, 37));
            chunks.Add(chunk);
            offset = chunk.TryGetProperty("nextOffset", out var nextOffset)
                ? nextOffset.GetInt32()
                : logic.Length;
        }
        while (chunks[^1].GetProperty("hasMore").GetBoolean());

        Assert.All(chunks, chunk => Assert.Equal(logic.Length, chunk.GetProperty("totalChars").GetInt32()));
        Assert.Equal(logic, string.Concat(chunks.Select(chunk => chunk.GetProperty("logicStatements").GetString())));
        Assert.True(chunks.Count > 3);
        Assert.True(chunks[0].GetProperty("hasMore").GetBoolean());
        Assert.Equal(37, chunks[0].GetProperty("nextOffset").GetInt32());
        Assert.False(chunks[^1].GetProperty("hasMore").GetBoolean());
        Assert.False(chunks[^1].TryGetProperty("nextOffset", out _));

        db.ReplaceNetworkLogic(new string('x', 12_000));
        var bounded = ToolResults.OkJson(tools.GetNetworkLogic(db.Path, "Main", 1));
        Assert.Equal(6_000, bounded.GetProperty("logicStatements").GetString()!.Length);
        Assert.True(bounded.GetRawText().Length < 8_000);
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

        public void ReplaceNetworkLogic(string logic)
        {
            using var connection = new SqliteConnection($"Data Source={Path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE knowledge_networks SET logic_statements = $logic WHERE network_id = 'network:Main:1';";
            command.Parameters.AddWithValue("$logic", logic);
            command.ExecuteNonQuery();
        }

        public void Dispose() => tree.Dispose();
    }
}
