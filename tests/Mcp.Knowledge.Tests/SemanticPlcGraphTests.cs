// Graph importer tests for the ported TiaXmlSemanticGraphImporter.
// Adapted from PlcSourceExporter (tests/PlcSourceExporter.Core.Tests/SemanticPlcGraphTests.cs):
// logicStatements assertions dropped (writer not ported), UDT/tag cases dropped (not in scope),
// expectations re-derived from the real V17 fixtures in Fixtures/.
using System;
using System.IO;
using System.Linq;
using Mcp.Knowledge.Graph;
using Mcp.Knowledge.Parsing;
using Xunit;

namespace Mcp.Knowledge.Tests;

public sealed class SemanticPlcGraphTests
{
    [Fact]
    public void ImportsRealObFixtureIntoCallNetworkAndSymbolNodes()
    {
        var graph = new SemanticPlcGraph();
        TiaXmlSemanticGraphImporter.ImportBlockXml(
            FixtureFiles.ReadAllText(FixtureFiles.MainObPath),
            new ProgramBlockComponent("Main", "OB", "Program blocks/Main", "Main [OB1].xml"),
            graph);

        Assert.Equal(SemanticNodeKind.OrganizationBlock, graph.GetNode("block:Main").Kind);
        Assert.Equal("Main [OB1].xml", graph.GetNode("block:Main").Properties["sourceFile"]);

        var network1 = graph.GetNode("network:Main:1");
        Assert.Equal(SemanticNodeKind.Network, network1.Kind);
        Assert.Equal("Network 1", network1.Properties["title"]);
        Assert.Equal("LAD", network1.Properties["language"]);
        Assert.Equal("1", network1.Properties["networkIndex"]);
        Assert.Equal("3", network1.Properties["compileUnitId"]);
        Assert.Equal(SemanticNodeKind.Network, graph.GetNode("network:Main:2").Kind);

        // Callee is not part of this import: placeholder node flagged declaredByReference.
        var callee = graph.GetNode("block:FC_LAD_SimulateCylinder_Call");
        Assert.Equal(SemanticNodeKind.Function, callee.Kind);
        Assert.Equal("true", callee.Properties["declaredByReference"]);

        var instruction = graph.GetNode("instruction:Main:1:call:1");
        Assert.Equal(SemanticNodeKind.Instruction, instruction.Kind);
        Assert.Equal("CALL", instruction.Properties["instructionKind"]);
        Assert.Equal("FC", instruction.Properties["calleeBlockType"]);

        Assert.Equal(SemanticNodeKind.Variable, graph.GetNode("symbol:Btn_ForwardCommand").Kind);
        Assert.Equal("GlobalVariable", graph.GetNode("symbol:Btn_ForwardCommand").Properties["scope"]);

        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Contains &&
            edge.FromNodeId == "block:Main" &&
            edge.ToNodeId == "network:Main:1");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.ExecutesBefore &&
            edge.FromNodeId == "network:Main:1" &&
            edge.ToNodeId == "network:Main:2");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.ExecutesAfter &&
            edge.FromNodeId == "network:Main:2" &&
            edge.ToNodeId == "network:Main:1");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Contains &&
            edge.FromNodeId == "network:Main:1" &&
            edge.ToNodeId == "instruction:Main:1:call:1");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Calls &&
            edge.FromNodeId == "instruction:Main:1:call:1" &&
            edge.ToNodeId == "block:FC_LAD_SimulateCylinder_Call");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Calls &&
            edge.FromNodeId == "block:Main" &&
            edge.ToNodeId == "block:FC_LAD_SimulateCylinder_Call" &&
            edge.Properties["networkId"] == "network:Main:1");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Reads &&
            edge.FromNodeId == "block:Main" &&
            edge.ToNodeId == "symbol:Btn_ForwardCommand" &&
            edge.Properties["parameter"] == "btn_forward");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Writes &&
            edge.FromNodeId == "block:Main" &&
            edge.ToNodeId == "symbol:CylinderGoForwardPos" &&
            edge.Properties["parameter"] == "outputGoForwardPos");

        // InOut parameters produce both READS and WRITES edges.
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Reads &&
            edge.FromNodeId == "block:Main" &&
            edge.ToNodeId == "symbol:Cylinder@ForwardPos");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Writes &&
            edge.FromNodeId == "block:Main" &&
            edge.ToNodeId == "symbol:Cylinder@ForwardPos");
    }

    [Fact]
    public void ImportsCraftedGlobalDbFixtureIntoMemberTree()
    {
        var graph = new SemanticPlcGraph();
        TiaXmlSemanticGraphImporter.ImportDbXml(
            FixtureFiles.ReadAllText(FixtureFiles.GlobalDataDbPath),
            "GlobalData [DB1].xml",
            "Program blocks/GlobalData",
            graph);

        var db = graph.GetNode("db:GlobalData");
        Assert.Equal(SemanticNodeKind.GlobalDataBlock, db.Kind);
        Assert.Equal("GlobalDB", db.Properties["dbType"]);

        Assert.Equal(SemanticNodeKind.DataBlockMember, graph.GetNode("db-member:GlobalData:Ready").Kind);
        Assert.Equal(SemanticNodeKind.DataBlockMember, graph.GetNode("db-member:GlobalData:Count").Kind);
        Assert.Equal(SemanticNodeKind.DataBlockMember, graph.GetNode("db-member:GlobalData:Config").Kind);
        var nested = graph.GetNode("db-member:GlobalData:Config.MaxSpeed");
        Assert.Equal("Config.MaxSpeed", nested.Properties["path"]);
        Assert.Equal(SemanticNodeKind.DataBlockMember, graph.GetNode("db-member:GlobalData:Config.Enabled").Kind);

        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Contains &&
            edge.FromNodeId == "db:GlobalData" &&
            edge.ToNodeId == "db-member:GlobalData:Config");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Contains &&
            edge.FromNodeId == "db-member:GlobalData:Config" &&
            edge.ToNodeId == "db-member:GlobalData:Config.MaxSpeed");

        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.HasType &&
            edge.FromNodeId == "db-member:GlobalData:Ready" &&
            edge.ToNodeId == "type:Bool");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.HasType &&
            edge.FromNodeId == "db-member:GlobalData:Config" &&
            edge.ToNodeId == "type:Struct");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.HasType &&
            edge.FromNodeId == "db-member:GlobalData:Config.MaxSpeed" &&
            edge.ToNodeId == "type:Real");
        Assert.Equal(SemanticNodeKind.DataType, graph.GetNode("type:Int").Kind);
    }

    [Fact]
    public void ImportsCraftedInstanceDbFixtureWithInstanceOfEdge()
    {
        var graph = new SemanticPlcGraph();
        TiaXmlSemanticGraphImporter.ImportDbXml(
            FixtureFiles.ReadAllText(FixtureFiles.MotorFbInstanceDbPath),
            "MotorFbInstance [DB2].xml",
            "Program blocks/MotorFbInstance",
            graph);

        var db = graph.GetNode("db:MotorFbInstance");
        Assert.Equal(SemanticNodeKind.InstanceDataBlock, db.Kind);
        Assert.Equal("InstanceDB", db.Properties["dbType"]);

        Assert.Equal(SemanticNodeKind.FunctionBlock, graph.GetNode("block:MotorFb").Kind);
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.InstanceOf &&
            edge.FromNodeId == "db:MotorFbInstance" &&
            edge.ToNodeId == "block:MotorFb");

        Assert.Equal(SemanticNodeKind.DataBlockMember, graph.GetNode("db-member:MotorFbInstance:Running").Kind);
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.HasType &&
            edge.FromNodeId == "db-member:MotorFbInstance:Setpoint" &&
            edge.ToNodeId == "type:Real");
    }

    [Fact]
    public void SqliteStoreRoundTripsFixtureGraph()
    {
        var graph = FixtureGraph.Build();
        var dbPath = TempDbPath();

        SqliteSemanticGraphStore.Save(dbPath, graph);
        var loaded = SqliteSemanticGraphStore.Load(dbPath);

        Assert.Equal(graph.Nodes.Count, loaded.Nodes.Count);
        Assert.Equal(graph.Edges.Count, loaded.Edges.Count);
        GraphAssert.Equal(graph, loaded);
    }

    [Fact]
    public void ImportProducesDeterministicIds()
    {
        var first = FixtureGraph.Build();
        var second = FixtureGraph.Build();

        Assert.Equal(first.Nodes.Select(node => node.Id).ToArray(), second.Nodes.Select(node => node.Id).ToArray());
        Assert.Equal(first.Edges.Select(edge => edge.Id).ToArray(), second.Edges.Select(edge => edge.Id).ToArray());
    }

    private static string TempDbPath()
    {
        return Path.Combine(Path.GetTempPath(), "Mcp.Knowledge.Tests", Guid.NewGuid().ToString("N"), "graph.db");
    }
}

internal static class FixtureGraph
{
    public static SemanticPlcGraph Build()
    {
        var graph = new SemanticPlcGraph();
        TiaXmlSemanticGraphImporter.ImportBlockXml(
            FixtureFiles.ReadAllText(FixtureFiles.MainObPath),
            new ProgramBlockComponent("Main", "OB", "Program blocks/Main", "Main [OB1].xml"),
            graph);
        TiaXmlSemanticGraphImporter.ImportBlockXml(
            FixtureFiles.ReadAllText(FixtureFiles.SimulateCylinderFcPath),
            new ProgramBlockComponent(
                "FC_LAD_SimulateCylinder_Call",
                "FC",
                "Program blocks/FC_LAD_SimulateCylinder_Call",
                "FC_LAD_SimulateCylinder_Call [FC1].xml"),
            graph);
        TiaXmlSemanticGraphImporter.ImportDbXml(
            FixtureFiles.ReadAllText(FixtureFiles.GlobalDataDbPath),
            "GlobalData [DB1].xml",
            "Program blocks/GlobalData",
            graph);
        TiaXmlSemanticGraphImporter.ImportDbXml(
            FixtureFiles.ReadAllText(FixtureFiles.MotorFbInstanceDbPath),
            "MotorFbInstance [DB2].xml",
            "Program blocks/MotorFbInstance",
            graph);
        return graph;
    }
}

internal static class GraphAssert
{
    public static void Equal(SemanticPlcGraph expected, SemanticPlcGraph actual)
    {
        Assert.Equal(expected.Nodes.Select(node => node.Id).ToArray(), actual.Nodes.Select(node => node.Id).ToArray());
        foreach (var expectedNode in expected.Nodes)
        {
            var actualNode = actual.GetNode(expectedNode.Id);
            Assert.Equal(expectedNode.Kind, actualNode.Kind);
            Assert.Equal(expectedNode.Name, actualNode.Name);
            Assert.Equal(
                expectedNode.Properties.OrderBy(item => item.Key, StringComparer.Ordinal),
                actualNode.Properties.OrderBy(item => item.Key, StringComparer.Ordinal));
        }

        Assert.Equal(expected.Edges.Select(edge => edge.Id).ToArray(), actual.Edges.Select(edge => edge.Id).ToArray());
        foreach (var expectedEdge in expected.Edges)
        {
            var actualEdge = actual.Edges.Single(edge => edge.Id == expectedEdge.Id);
            Assert.Equal(expectedEdge.FromNodeId, actualEdge.FromNodeId);
            Assert.Equal(expectedEdge.ToNodeId, actualEdge.ToNodeId);
            Assert.Equal(expectedEdge.Type, actualEdge.Type);
            Assert.Equal(
                expectedEdge.Properties.OrderBy(item => item.Key, StringComparer.Ordinal),
                actualEdge.Properties.OrderBy(item => item.Key, StringComparer.Ordinal));
        }
    }
}
