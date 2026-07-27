using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mcp.Knowledge.Graph;
using Mcp.Knowledge.Import;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Mcp.Knowledge.Tests;

public sealed class ComponentUpdateToolTests
{
    private const string MainPath = "Blocks/Main [OB1].xml";
    private const string CalleePath = "Blocks/FC_LAD_SimulateCylinder_Call [FC1].xml";

    [Fact]
    public void FullSaveRecordsEachComponentsOwnedNodesAndEdges()
    {
        using var fixture = CreateSharedCalleeFixture();
        var imported = ExportFolderCrawler.Import(fixture.Tree.Root);
        var dbPath = Path.Combine(fixture.Tree.Root, "knowledge.db");

        SqliteSemanticGraphStore.Save(dbPath, imported.Graph);

        Assert.Equal(2, Scalar(dbPath, "SELECT COUNT(*) FROM source_components;"));
        Assert.True(Scalar(
            dbPath,
            """
            SELECT COUNT(*)
            FROM source_component_nodes
            WHERE component_key = $component_key AND node_id = 'network:Main:1';
            """,
            ("$component_key", fixture.MainKey)) > 0);
        Assert.True(Scalar(
            dbPath,
            """
            SELECT COUNT(*)
            FROM source_component_edges ownership
            JOIN graph_edges edge ON edge.id = ownership.edge_id
            WHERE ownership.component_key = $component_key
              AND edge.from_node_id = 'block:Main'
              AND edge.to_node_id = 'block:FC_LAD_SimulateCylinder_Call'
              AND edge.type = 'CALLS';
            """,
            ("$component_key", fixture.MainKey)) > 0);

        var sharedOwners = Scalar(
            dbPath,
            """
            SELECT COUNT(*)
            FROM source_component_nodes
            WHERE node_id = 'block:FC_LAD_SimulateCylinder_Call';
            """);
        Assert.Equal(2, sharedOwners);
    }

    [Fact]
    public void ReplacingComponentRemovesItsNetworksAndEdgesButPreservesSharedCallee()
    {
        using var fixture = CreateSharedCalleeFixture();
        var dbPath = Save(fixture);
        fixture.Tree.AddText(MainPath, EmptyOb("Main"));

        ComponentProvenanceStore.Replace(dbPath, fixture.Tree.Root, MainPath);

        var graph = SqliteSemanticGraphStore.Load(dbPath);
        Assert.DoesNotContain(graph.Nodes, node => node.Id.StartsWith("network:Main:", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Edges, edge =>
            edge.FromNodeId == "block:Main" &&
            edge.ToNodeId == "block:FC_LAD_SimulateCylinder_Call");
        Assert.Equal(
            SemanticNodeKind.Function,
            graph.GetNode("block:FC_LAD_SimulateCylinder_Call").Kind);
        Assert.False(graph.GetNode("block:FC_LAD_SimulateCylinder_Call")
            .Properties.ContainsKey("declaredByReference"));
        Assert.Equal(
            1,
            Scalar(
                dbPath,
                """
                SELECT COUNT(*)
                FROM source_component_nodes
                WHERE node_id = 'block:FC_LAD_SimulateCylinder_Call';
                """));
    }

    [Fact]
    public void ReplacingOneOwnerPreservesSharedNodesAndEdgesOwnedByAnotherComponent()
    {
        using var tree = new TempExportTree();
        const string firstPath = "Tags/First.xml";
        const string secondPath = "Tags/Second.xml";
        tree.AddFixture(FixtureFiles.IoCcTagTablePath, firstPath);
        tree.AddFixture(FixtureFiles.IoCcTagTablePath, secondPath);
        ManifestFixtures.Write(
            tree,
            ManifestFixtures.Component("IO_CC_Cav_A", "Tags", firstPath, "PLC tags/First"),
            ManifestFixtures.Component("IO_CC_Cav_A", "Tags", secondPath, "PLC tags/Second"));
        var imported = ExportFolderCrawler.Import(tree.Root);
        var sharedEdge = imported.Graph.Edges.First(edge =>
            edge.FromNodeId == "tag:IO_CC_Cav_A:O_CC_Coupelle_Up_Cav_A:%Q600.7" &&
            edge.Type == SemanticRelationshipType.HasType);
        var dbPath = Path.Combine(tree.Root, "knowledge.db");
        SqliteSemanticGraphStore.Save(dbPath, imported.Graph);
        Assert.Equal(
            2,
            Scalar(
                dbPath,
                "SELECT COUNT(*) FROM source_component_edges WHERE edge_id = $edge_id;",
                ("$edge_id", sharedEdge.Id)));
        tree.AddText(firstPath, EmptyTagTable("IO_CC_Cav_A"));

        ComponentProvenanceStore.Replace(dbPath, tree.Root, firstPath);

        var graph = SqliteSemanticGraphStore.Load(dbPath);
        Assert.Equal(
            SemanticNodeKind.PlcTag,
            graph.GetNode("tag:IO_CC_Cav_A:O_CC_Coupelle_Up_Cav_A:%Q600.7").Kind);
        Assert.Contains(graph.Edges, edge => edge.Id == sharedEdge.Id);
        Assert.Equal(
            1,
            Scalar(
                dbPath,
                "SELECT COUNT(*) FROM source_component_edges WHERE edge_id = $edge_id;",
                ("$edge_id", sharedEdge.Id)));
    }

    [Fact]
    public void MalformedReplacementLeavesGraphAndProvenanceUnchanged()
    {
        using var fixture = CreateSharedCalleeFixture();
        var dbPath = Save(fixture);
        var before = DatabaseSnapshot(dbPath);
        fixture.Tree.AddText(MainPath, "<Document><SW.Blocks.OB>");

        Assert.Throws<ManifestInvalidException>(() =>
            ComponentProvenanceStore.Replace(dbPath, fixture.Tree.Root, MainPath));

        Assert.Equal(before, DatabaseSnapshot(dbPath));
    }

    [Fact]
    public void ReplacementWhoseXmlIdentityDoesNotMatchItsManifestPathIsRejected()
    {
        using var fixture = CreateSharedCalleeFixture();
        var dbPath = Save(fixture);
        var before = DatabaseSnapshot(dbPath);
        fixture.Tree.AddText(MainPath, EmptyOb("DifferentBlock"));

        var error = Assert.Throws<ComponentIdentityMismatchException>(() =>
            ComponentProvenanceStore.Replace(dbPath, fixture.Tree.Root, MainPath));

        Assert.Equal("COMPONENT_IDENTITY_MISMATCH", error.Code);
        Assert.Equal(before, DatabaseSnapshot(dbPath));
    }

    [Fact]
    public void LoadThenSavePreservesProvenanceForLaterReplacement()
    {
        using var fixture = CreateSharedCalleeFixture();
        var dbPath = Save(fixture);

        var loaded = SqliteSemanticGraphStore.Load(dbPath);
        SqliteSemanticGraphStore.Save(dbPath, loaded);
        fixture.Tree.AddText(MainPath, EmptyOb("Main"));
        ComponentProvenanceStore.Replace(dbPath, fixture.Tree.Root, MainPath);

        Assert.DoesNotContain(
            SqliteSemanticGraphStore.Load(dbPath).Nodes,
            node => node.Id.StartsWith("network:Main:", StringComparison.Ordinal));
        Assert.Equal(2, Scalar(dbPath, "SELECT COUNT(*) FROM source_components;"));
    }

    [Fact]
    public void MultiDeviceReplacementKeepsCombinedProjectContainment()
    {
        using var tree = new TempExportTree();
        const string deviceName = "PLC_A";
        var deviceRoot = Path.Combine(tree.Root, deviceName);
        tree.AddFixture(FixtureFiles.MainObPath, Path.Combine(deviceName, MainPath));
        tree.AddText(
            Path.Combine(deviceName, "metadata.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                components = new[]
                {
                    ManifestFixtures.Component(
                        "Main",
                        "OB",
                        MainPath,
                        "Program blocks/Main"),
                },
            }));
        var dbPath = Path.Combine(tree.Root, "knowledge.db");
        SqliteSemanticGraphStore.Save(
            dbPath,
            ExportFolderCrawler.Import(tree.Root).Graph);
        var combinedProjectId = $"project:{Path.GetFileName(tree.Root)}";
        Assert.True(EdgeExists(
            dbPath,
            combinedProjectId,
            $"{deviceName}/block:Main",
            SemanticRelationshipType.Contains));
        tree.AddText(Path.Combine(deviceName, MainPath), EmptyOb("Main"));

        ComponentProvenanceStore.Replace(
            dbPath,
            deviceRoot,
            MainPath,
            deviceName);

        Assert.True(EdgeExists(
            dbPath,
            combinedProjectId,
            $"{deviceName}/block:Main",
            SemanticRelationshipType.Contains));
        Assert.Equal(
            0,
            Scalar(
                dbPath,
                "SELECT COUNT(*) FROM graph_nodes WHERE id = 'project:PLC_A';"));
    }

    [Fact]
    public void MultiDeviceTagReplacementDoesNotInsertDeviceProjectNode()
    {
        using var tree = new TempExportTree();
        const string deviceName = "PLC_A";
        const string tagPath = "Tags/IO.xml";
        var deviceRoot = Path.Combine(tree.Root, deviceName);
        tree.AddFixture(
            FixtureFiles.IoCcTagTablePath,
            Path.Combine(deviceName, tagPath));
        tree.AddText(
            Path.Combine(deviceName, "metadata.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                components = new[]
                {
                    ManifestFixtures.Component(
                        "IO_CC_Cav_A",
                        "Tags",
                        tagPath,
                        "PLC tags/IO_CC_Cav_A"),
                },
            }));
        var dbPath = Path.Combine(tree.Root, "knowledge.db");
        SqliteSemanticGraphStore.Save(
            dbPath,
            ExportFolderCrawler.Import(tree.Root).Graph);
        tree.AddText(
            Path.Combine(deviceName, tagPath),
            EmptyTagTable("IO_CC_Cav_A"));

        ComponentProvenanceStore.Replace(
            dbPath,
            deviceRoot,
            tagPath,
            deviceName);

        Assert.Equal(
            0,
            Scalar(
                dbPath,
                "SELECT COUNT(*) FROM graph_nodes WHERE id = 'project:PLC_A';"));
        Assert.Equal(
            1,
            Scalar(
                dbPath,
                "SELECT COUNT(*) FROM graph_nodes WHERE kind = 'Project';"));
    }

    [Fact]
    public void PreProvenanceDatabaseRequiresRebuildWithoutChangingSchema()
    {
        using var fixture = CreateSharedCalleeFixture();
        var dbPath = Path.Combine(fixture.Tree.Root, "legacy.db");
        CreateLegacyGraphDatabase(dbPath);
        var tablesBefore = TableNames(dbPath);

        var error = Assert.Throws<ComponentProvenanceUnavailableException>(() =>
            ComponentProvenanceStore.Replace(
                dbPath,
                fixture.Tree.Root,
                MainPath));

        Assert.Equal("COMPONENT_PROVENANCE_REBUILD_REQUIRED", error.Code);
        Assert.Equal(tablesBefore, TableNames(dbPath));
    }

    private static SharedCalleeFixture CreateSharedCalleeFixture()
    {
        var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.MainObPath, MainPath);
        tree.AddFixture(FixtureFiles.SimulateCylinderFcPath, CalleePath);
        var main = ManifestFixtures.Component("Main", "OB", MainPath, "Program blocks/Main");
        var callee = ManifestFixtures.Component(
            "FC_LAD_SimulateCylinder_Call",
            "FC",
            CalleePath,
            "Program blocks/FC_LAD_SimulateCylinder_Call");
        ManifestFixtures.Write(tree, main, callee);

        return new SharedCalleeFixture(
            tree,
            ComponentKey("OB", "Program blocks/Main"),
            ComponentKey("FC", "Program blocks/FC_LAD_SimulateCylinder_Call"));
    }

    private static string Save(SharedCalleeFixture fixture)
    {
        var dbPath = Path.Combine(fixture.Tree.Root, "knowledge.db");
        SqliteSemanticGraphStore.Save(
            dbPath,
            ExportFolderCrawler.Import(fixture.Tree.Root).Graph);
        return dbPath;
    }

    private static string ComponentKey(string category, string sourcePath)
    {
        return Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{category}|{sourcePath}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string EmptyOb(string name)
    {
        return $$"""
            <Document>
              <SW.Blocks.OB ID="0">
                <AttributeList>
                  <Name>{{name}}</Name>
                  <ProgrammingLanguage>LAD</ProgrammingLanguage>
                </AttributeList>
                <ObjectList />
              </SW.Blocks.OB>
            </Document>
            """;
    }

    private static string EmptyTagTable(string name)
    {
        return $$"""
            <Document>
              <SW.Tags.PlcTagTable ID="0">
                <AttributeList>
                  <Name>{{name}}</Name>
                </AttributeList>
                <ObjectList />
              </SW.Tags.PlcTagTable>
            </Document>
            """;
    }

    private static long Scalar(
        string dbPath,
        string sql,
        params (string Name, string Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static bool EdgeExists(
        string dbPath,
        string fromNodeId,
        string toNodeId,
        string type)
    {
        return Scalar(
            dbPath,
            """
            SELECT COUNT(*)
            FROM graph_edges
            WHERE from_node_id = $from_node_id
              AND to_node_id = $to_node_id
              AND type = $type;
            """,
            ("$from_node_id", fromNodeId),
            ("$to_node_id", toNodeId),
            ("$type", type)) > 0;
    }

    private static void CreateLegacyGraphDatabase(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE graph_nodes (
              id TEXT NOT NULL PRIMARY KEY,
              kind TEXT NOT NULL,
              name TEXT NOT NULL
            );
            CREATE TABLE graph_node_properties (
              node_id TEXT NOT NULL,
              name TEXT NOT NULL,
              value TEXT NOT NULL,
              PRIMARY KEY (node_id, name)
            );
            CREATE TABLE graph_edges (
              id TEXT NOT NULL PRIMARY KEY,
              from_node_id TEXT NOT NULL,
              to_node_id TEXT NOT NULL,
              type TEXT NOT NULL
            );
            CREATE TABLE graph_edge_properties (
              edge_id TEXT NOT NULL,
              name TEXT NOT NULL,
              value TEXT NOT NULL,
              PRIMARY KEY (edge_id, name)
            );
            INSERT INTO graph_nodes (id, kind, name)
            VALUES ('block:Main', 'OB', 'Main');
            """;
        command.ExecuteNonQuery();
    }

    private static string[] TableNames(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
            ORDER BY name;
            """;
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables.ToArray();
    }

    private static string DatabaseSnapshot(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'node|' || id || '|' || kind || '|' || name FROM graph_nodes
            UNION ALL
            SELECT 'node-property|' || node_id || '|' || name || '|' || value FROM graph_node_properties
            UNION ALL
            SELECT 'edge|' || id || '|' || from_node_id || '|' || to_node_id || '|' || type FROM graph_edges
            UNION ALL
            SELECT 'edge-property|' || edge_id || '|' || name || '|' || value FROM graph_edge_properties
            UNION ALL
            SELECT 'component|' || component_key || '|' || relative_path || '|' || content_hash FROM source_components
            UNION ALL
            SELECT 'component-node|' || component_key || '|' || node_id FROM source_component_nodes
            UNION ALL
            SELECT 'component-edge|' || component_key || '|' || edge_id FROM source_component_edges
            ORDER BY 1;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(reader.GetString(0));
        }

        return string.Join('\n', rows);
    }

    private sealed record SharedCalleeFixture(
        TempExportTree Tree,
        string MainKey,
        string CalleeKey) : IDisposable
    {
        public void Dispose() => Tree.Dispose();
    }
}
