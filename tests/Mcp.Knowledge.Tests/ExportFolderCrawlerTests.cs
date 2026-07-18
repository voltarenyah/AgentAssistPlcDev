using System.IO;
using System.Linq;
using Mcp.Knowledge.Graph;
using Mcp.Knowledge.Import;
using Xunit;

namespace Mcp.Knowledge.Tests;

public sealed class ExportFolderCrawlerTests
{
    [Fact]
    public void SkipsDuplicateBlocksInNestedFolders()
    {
        using var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.MainObPath, "Main [OB1].xml");
        tree.AddFixture(FixtureFiles.SimulateCylinderFcPath, "FC_LAD_SimulateCylinder_Call [FC1].xml");
        // Mirrors the real exported/TestPLCExportDemo/spike/reexport/ situation.
        tree.AddFixture(FixtureFiles.SimulateCylinderFcPath, Path.Combine("spike", "reexport", "FC_LAD_SimulateCylinder_Call [FC1].xml"));

        var result = ExportFolderCrawler.Import(tree.Root);

        Assert.Equal(3, result.FilesFound);
        Assert.Equal(2, result.FilesImported);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("skipped duplicate", warning);
        Assert.Contains("spike", warning);
        Assert.Contains("FC_LAD_SimulateCylinder_Call", warning);

        // The shallowest file wins: the block carries the root file as its sourceFile.
        var fc = result.Graph.GetNode("block:FC_LAD_SimulateCylinder_Call");
        Assert.Equal("FC_LAD_SimulateCylinder_Call [FC1].xml", fc.Properties["sourceFile"]);
        // Main's call references the FC; the placeholder upsert must not clobber the real block node
        // (FC sorts before Main alphabetically, so Main's import runs later).
        Assert.False(fc.Properties.ContainsKey("declaredByReference"));
    }

    [Fact]
    public void BreaksDuplicateTiesAlphabeticallyAtSameDepth()
    {
        using var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.SimulateCylinderFcPath, Path.Combine("b", "FC_LAD_SimulateCylinder_Call [FC1].xml"));
        tree.AddFixture(FixtureFiles.SimulateCylinderFcPath, Path.Combine("a", "FC_LAD_SimulateCylinder_Call [FC1].xml"));

        var result = ExportFolderCrawler.Import(tree.Root);

        Assert.Equal(2, result.FilesFound);
        Assert.Equal(1, result.FilesImported);
        Assert.Single(result.Warnings);
        Assert.Equal(
            Path.Combine("a", "FC_LAD_SimulateCylinder_Call [FC1].xml"),
            result.Graph.GetNode("block:FC_LAD_SimulateCylinder_Call").Properties["sourceFile"]);
    }

    [Fact]
    public void SkipsUnsupportedAndMalformedFilesWithWarnings()
    {
        using var tree = new TempExportTree();
        tree.AddText("Hardware.xml", """
            <Document><SW.HW.Device ID="0"><AttributeList><Name>PLC_1</Name></AttributeList></SW.HW.Device></Document>
            """);
        tree.AddText("NotTia.xml", "<Root><Whatever /></Root>");
        tree.AddText("Broken.xml", "<Document><SW.Blocks.OB ID=\"0\">");

        var result = ExportFolderCrawler.Import(tree.Root);

        Assert.Equal(3, result.FilesFound);
        Assert.Equal(0, result.FilesImported);
        Assert.Equal(3, result.Warnings.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("Hardware.xml") && warning.Contains("unsupported root element"));
        Assert.Contains(result.Warnings, warning => warning.Contains("NotTia.xml") && warning.Contains("no SW.* content element"));
        Assert.Contains(result.Warnings, warning => warning.Contains("Broken.xml") && warning.Contains("malformed XML"));
    }

    [Fact]
    public void ClassifiesUdtAndTagTableRootElements()
    {
        using var tree = new TempExportTree();
        tree.AddText("UDT_Motor.xml", """
            <Document><SW.Types.PlcStruct ID="0"><AttributeList><Name>UDT_Motor</Name></AttributeList></SW.Types.PlcStruct></Document>
            """);
        tree.AddText("Default tag table.xml", """
            <Document><SW.Tags.PlcTagTable ID="0"><AttributeList><Name>Default tag table</Name></AttributeList><ObjectList><SW.Tags.PlcTag ID="1" CompositionName="Tags"><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.0</LogicalAddress><Name>MotorRunning</Name></AttributeList></SW.Tags.PlcTag></ObjectList></SW.Tags.PlcTagTable></Document>
            """);

        var result = ExportFolderCrawler.Import(tree.Root);

        Assert.Equal(2, result.FilesFound);
        Assert.Equal(2, result.FilesImported);
        Assert.Empty(result.Warnings);
        Assert.Equal(SemanticNodeKind.UserDataType, result.Graph.GetNode("udt:UDT_Motor").Kind);
        Assert.Equal(SemanticNodeKind.PlcTag, result.Graph.GetNode("tag:Default tag table:MotorRunning:%M0.0").Kind);
    }

    [Fact]
    public void AddsProjectNodeWithContainsEdgesToTopLevelObjects()
    {
        using var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.MainObPath, "Main [OB1].xml");
        tree.AddFixture(FixtureFiles.SimulateCylinderFcPath, "FC_LAD_SimulateCylinder_Call [FC1].xml");
        tree.AddFixture(FixtureFiles.GlobalDataDbPath, "GlobalData [DB1].xml");
        tree.AddFixture(FixtureFiles.MotorFbInstanceDbPath, "MotorFbInstance [DB2].xml");

        var result = ExportFolderCrawler.Import(tree.Root);

        var project = Assert.Single(result.Graph.Nodes, node => node.Kind == SemanticNodeKind.Project);
        Assert.Equal($"project:{Path.GetFileName(tree.Root)}", project.Id);
        Assert.Contains(result.Graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Contains &&
            edge.FromNodeId == project.Id &&
            edge.ToNodeId == "block:Main");
        Assert.Contains(result.Graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Contains &&
            edge.FromNodeId == project.Id &&
            edge.ToNodeId == "block:FC_LAD_SimulateCylinder_Call");
        Assert.Contains(result.Graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Contains &&
            edge.FromNodeId == project.Id &&
            edge.ToNodeId == "db:GlobalData");
        Assert.Contains(result.Graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Contains &&
            edge.FromNodeId == project.Id &&
            edge.ToNodeId == "db:MotorFbInstance");

        // Networks and DB members are contained by their blocks, not directly by the project.
        Assert.DoesNotContain(result.Graph.Edges, edge =>
            edge.FromNodeId == project.Id && edge.ToNodeId.StartsWith("network:"));
    }
}
