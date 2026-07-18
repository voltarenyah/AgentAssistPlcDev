using System.IO;
using System.Linq;
using Mcp.Knowledge.Graph;
using Mcp.Knowledge.Import;
using Mcp.Knowledge.Tools;
using Xunit;

namespace Mcp.Knowledge.Tests;

public sealed class IngestSourceToolTests
{
    [Fact]
    public void IngestsFixtureTreeEndToEndIntoTempDb()
    {
        using var tree = CreateFixtureTree();
        var tools = new KnowledgeTools();

        var result = ToolResults.OkJson(tools.IngestSource(tree.Root, null));

        // Default dbPath is <exportRoot>/plc-knowledge.db.
        var expectedDbPath = Path.Combine(tree.Root, "plc-knowledge.db");
        Assert.Equal(expectedDbPath, result.GetProperty("dbPath").GetString());
        Assert.True(File.Exists(expectedDbPath));
        Assert.Equal(4, result.GetProperty("filesFound").GetInt32());
        Assert.Equal(4, result.GetProperty("filesImported").GetInt32());
        Assert.Equal(0, result.GetProperty("warnings").GetArrayLength());

        // Counts match a direct crawler import of the same tree.
        var direct = ExportFolderCrawler.Import(tree.Root);
        Assert.Equal(direct.Graph.Nodes.Count, result.GetProperty("nodes").GetInt32());
        Assert.Equal(direct.Graph.Edges.Count, result.GetProperty("edges").GetInt32());

        var byKind = result.GetProperty("byKind");
        Assert.Equal(1, byKind.GetProperty("OB").GetInt32());
        Assert.Equal(1, byKind.GetProperty("FC").GetInt32());
        Assert.Equal(9, byKind.GetProperty("Network").GetInt32());
        Assert.Equal(1, byKind.GetProperty("Instruction").GetInt32());
        Assert.Equal(1, byKind.GetProperty("Project").GetInt32());
        Assert.Equal(1, byKind.GetProperty("Global DB").GetInt32());
        Assert.Equal(1, byKind.GetProperty("Instance DB").GetInt32());
        Assert.Equal(7, byKind.GetProperty("DB Member").GetInt32());

        // Persisted DB: project node + CONTAINS edges to the four top-level objects.
        var loaded = SqliteSemanticGraphStore.Load(expectedDbPath);
        var projectId = $"project:{Path.GetFileName(tree.Root)}";
        Assert.Equal(SemanticNodeKind.Project, loaded.GetNode(projectId).Kind);
        foreach (var target in new[] { "block:Main", "block:FC_LAD_SimulateCylinder_Call", "db:GlobalData", "db:MotorFbInstance" })
        {
            Assert.Contains(loaded.Edges, edge =>
                edge.Type == SemanticRelationshipType.Contains &&
                edge.FromNodeId == projectId &&
                edge.ToNodeId == target);
        }
    }

    [Fact]
    public void ReIngestYieldsIdenticalIds()
    {
        using var tree = CreateFixtureTree();
        var tools = new KnowledgeTools();
        var firstDb = Path.Combine(tree.Root, "first.db");
        var secondDb = Path.Combine(tree.Root, "second.db");

        ToolResults.OkJson(tools.IngestSource(tree.Root, firstDb));
        ToolResults.OkJson(tools.IngestSource(tree.Root, secondDb));

        var first = SqliteSemanticGraphStore.Load(firstDb);
        var second = SqliteSemanticGraphStore.Load(secondDb);
        Assert.Equal(first.Nodes.Select(node => node.Id).ToArray(), second.Nodes.Select(node => node.Id).ToArray());
        Assert.Equal(first.Edges.Select(edge => edge.Id).ToArray(), second.Edges.Select(edge => edge.Id).ToArray());
    }

    [Fact]
    public void RejectsMissingExportRoot()
    {
        var tools = new KnowledgeTools();
        var missing = Path.Combine(Path.GetTempPath(), "Mcp.Knowledge.Tests", "does-not-exist");

        var error = ToolResults.ErrorJson(tools.IngestSource(missing, null));

        Assert.Equal("EXPORT_ROOT_NOT_FOUND", error.GetProperty("code").GetString());
    }

    [Fact]
    public void RejectsTreeWithoutImportableFiles()
    {
        using var tree = new TempExportTree();
        tree.AddText("junk.xml", "<Root />");
        var tools = new KnowledgeTools();

        var error = ToolResults.ErrorJson(tools.IngestSource(tree.Root, null));

        Assert.Equal("NO_SOURCE_FILES", error.GetProperty("code").GetString());
        Assert.Contains("junk.xml", error.GetProperty("message").GetString());
    }

    private static TempExportTree CreateFixtureTree()
    {
        var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.MainObPath, "Main [OB1].xml");
        tree.AddFixture(FixtureFiles.SimulateCylinderFcPath, "FC_LAD_SimulateCylinder_Call [FC1].xml");
        tree.AddFixture(FixtureFiles.GlobalDataDbPath, "GlobalData [DB1].xml");
        tree.AddFixture(FixtureFiles.MotorFbInstanceDbPath, "MotorFbInstance [DB2].xml");
        return tree;
    }
}
