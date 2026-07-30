// Tests for the agent-performance improvements (buildnote/plan/agent-performance-improvement.md):
// I1 unclassified accesses still produce edges, I2 symbol→db-member REFERS_TO links,
// I4 get_variable_usage, I9 get_schema version short-circuit, I10 get_network compact mode.
using System;
using System.IO;
using System.Linq;
using Mcp.Knowledge.Graph;
using Mcp.Knowledge.Parsing;
using Mcp.Knowledge.Tools;
using Xunit;

namespace Mcp.Knowledge.Tests;

public sealed class KnowledgeImprovementsTests : IDisposable
{
    private readonly string _dbPath;

    public KnowledgeImprovementsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "Mcp.Knowledge.Tests", Guid.NewGuid().ToString("N"), "usage.db");
    }

    [Fact]
    public void SclAssignmentTargetYieldsWriteEdgeAndUnclassifiedAccessYieldsReadsFallback()
    {
        var graph = BuildSclGraph();

        // I1: "GlobalData.Count := ..." — the target before ':=' is a write.
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Writes &&
            edge.FromNodeId == "block:SclAssign" &&
            edge.ToNodeId == "symbol:GlobalData.Count");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Writes &&
            edge.FromNodeId == "network:SclAssign:1" &&
            edge.ToNodeId == "symbol:GlobalData.Config.MaxSpeed");

        // I1 fallback: the assignment source and the IF condition have no determinable
        // direction — previously dropped, now an over-inclusive READS edge.
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Reads &&
            edge.FromNodeId == "network:SclAssign:1" &&
            edge.ToNodeId == "symbol:GlobalData.Ready");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Reads &&
            edge.FromNodeId == "network:SclAssign:2" &&
            edge.ToNodeId == "symbol:GlobalData.Config.Enabled");

        // I1: the SCL write classification must not leak onto the fallback side —
        // the IF-condition access must not become a write.
        Assert.DoesNotContain(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Writes &&
            edge.ToNodeId == "symbol:GlobalData.Config.Enabled");
    }

    [Fact]
    public void LinksNestedStructSymbolsToDeepestDbMember()
    {
        var graph = BuildSclGraph();

        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.RefersTo &&
            edge.FromNodeId == "symbol:GlobalData.Config.MaxSpeed" &&
            edge.ToNodeId == "db-member:GlobalData:Config.MaxSpeed");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.RefersTo &&
            edge.FromNodeId == "symbol:GlobalData.Config.Enabled" &&
            edge.ToNodeId == "db-member:GlobalData:Config.Enabled");

        // Single-segment symbols (no DB prefix) stay unlinked.
        Assert.DoesNotContain(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.RefersTo &&
            edge.FromNodeId == "symbol:GlobalData");
    }

    [Fact]
    public void VariableUsageFindsUsageViaEdgesAndTextWithDirections()
    {
        SaveGraph(BuildSclGraph());
        var tools = new KnowledgeTools();

        var byPath = ToolResults.OkJson(tools.GetVariableUsage(_dbPath, "GlobalData.Config.MaxSpeed", null));
        Assert.Contains("db-member:GlobalData:Config.MaxSpeed",
            byPath.GetProperty("matchedNodes").EnumerateArray().Select(item => item.GetString()));
        var writeRow = Assert.Single(byPath.GetProperty("usages").EnumerateArray());
        Assert.Equal("SclAssign", writeRow.GetProperty("block").GetString());
        Assert.Equal("FC", writeRow.GetProperty("blockKind").GetString());
        Assert.Equal(1, writeRow.GetProperty("networkIndex").GetInt32());
        Assert.Equal("Write counters", writeRow.GetProperty("networkTitle").GetString());
        Assert.Equal("write", writeRow.GetProperty("access").GetString());
        Assert.Equal("network:SclAssign:1", writeRow.GetProperty("networkId").GetString());

        // Leaf-name resolution takes the same suffix path.
        var byLeaf = ToolResults.OkJson(tools.GetVariableUsage(_dbPath, "MaxSpeed", null));
        Assert.Single(byLeaf.GetProperty("usages").EnumerateArray());

        // IF-condition access: edge direction read (fallback) — no "mention" row for that network.
        var enabled = ToolResults.OkJson(tools.GetVariableUsage(_dbPath, "GlobalData.Config.Enabled", null));
        var enabledRow = Assert.Single(enabled.GetProperty("usages").EnumerateArray());
        Assert.Equal("read", enabledRow.GetProperty("access").GetString());
        Assert.Equal(2, enabledRow.GetProperty("networkIndex").GetInt32());

        // Text-only hits (no symbol/db-member node matches) are labeled "mention".
        var mention = ToolResults.OkJson(tools.GetVariableUsage(_dbPath, "GlobalData", null));
        var mentionRows = mention.GetProperty("usages").EnumerateArray().ToArray();
        Assert.Equal(2, mentionRows.Length);
        Assert.All(mentionRows, row => Assert.Equal("mention", row.GetProperty("access").GetString()));
        Assert.Empty(mention.GetProperty("matchedNodes").EnumerateArray());
    }

    [Fact]
    public void GetSchemaReturnsShortPayloadWhenKnownVersionMatches()
    {
        var tools = new KnowledgeTools();

        var full = ToolResults.OkJson(tools.GetSchema());
        var version = full.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Contains("graph_nodes", full.GetProperty("ddl").GetString());

        var skipped = ToolResults.OkJson(tools.GetSchema(version));
        Assert.True(skipped.GetProperty("unchanged").GetBoolean());
        Assert.Equal(version, skipped.GetProperty("version").GetString());
        Assert.False(skipped.TryGetProperty("ddl", out _));

        var stale = ToolResults.OkJson(tools.GetSchema("not-the-version"));
        Assert.True(stale.TryGetProperty("ddl", out _));
    }

    [Fact]
    public void GetNetworkCompactModeOmitsBlockMetadata()
    {
        SaveGraph(FixtureGraph.Build());
        var tools = new KnowledgeTools();

        var full = ToolResults.OkJson(tools.GetNetwork(_dbPath, "Main", 1));
        var fullBlock = full.GetProperty("block");
        Assert.Equal("block:Main", fullBlock.GetProperty("id").GetString());
        Assert.Equal("Main", fullBlock.GetProperty("name").GetString());
        Assert.True(fullBlock.TryGetProperty("kind", out _));
        Assert.True(fullBlock.TryGetProperty("sourceFile", out _));

        var compact = ToolResults.OkJson(tools.GetNetwork(_dbPath, "Main", 1, compact: true));
        var compactBlock = compact.GetProperty("block");
        Assert.Equal("block:Main", compactBlock.GetProperty("id").GetString());
        Assert.Equal("Main", compactBlock.GetProperty("name").GetString());
        Assert.False(compactBlock.TryGetProperty("kind", out _));
        Assert.False(compactBlock.TryGetProperty("sourceFile", out _));
        Assert.False(compactBlock.TryGetProperty("folderPath", out _));

        // Network payload and edge lists stay identical to the full mode.
        Assert.Equal(
            full.GetProperty("network").GetRawText(),
            compact.GetProperty("network").GetRawText());
        Assert.Equal(
            full.GetProperty("reads").GetRawText(),
            compact.GetProperty("reads").GetRawText());
    }

    private static SemanticPlcGraph BuildSclGraph()
    {
        var graph = new SemanticPlcGraph();
        TiaXmlSemanticGraphImporter.ImportDbXml(
            FixtureFiles.ReadAllText(FixtureFiles.GlobalDataDbPath),
            "GlobalData [DB1].xml",
            "Program blocks/GlobalData",
            graph);
        TiaXmlSemanticGraphImporter.ImportBlockXml(
            FixtureFiles.ReadAllText(FixtureFiles.SclAssignFcPath),
            new ProgramBlockComponent("SclAssign", "FC", "Program blocks/SclAssign", "SclAssign [FC10].xml"),
            graph);
        TiaXmlSemanticGraphImporter.LinkSymbolsToDbMembers(graph);
        return graph;
    }

    private void SaveGraph(SemanticPlcGraph graph)
    {
        SqliteSemanticGraphStore.Save(_dbPath, graph);
    }

    public void Dispose()
    {
        try
        {
            var directory = Path.GetDirectoryName(_dbPath);
            if (directory != null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }
}
