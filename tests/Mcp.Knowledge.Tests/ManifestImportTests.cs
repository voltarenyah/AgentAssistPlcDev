using System.IO;
using System.Linq;
using Mcp.Knowledge.Graph;
using Mcp.Knowledge.Tools;
using Xunit;

namespace Mcp.Knowledge.Tests;

public sealed class ManifestImportTests
{
    [Fact]
    public void ManifestDrivenIngestImportsBlocksAndDb()
    {
        using var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.MainObPath, Path.Combine("Blocks", "Main [OB1].xml"));
        tree.AddFixture(FixtureFiles.SimulateCylinderFcPath, Path.Combine("Blocks", "FC_LAD_SimulateCylinder_Call [FC1].xml"));
        tree.AddFixture(FixtureFiles.GlobalDataDbPath, Path.Combine("DB", "GlobalData [DB1].xml"));
        ManifestFixtures.Write(tree,
            ManifestFixtures.Component("Main", "OB", @"Blocks\Main [OB1].xml", "Program blocks/Main"),
            ManifestFixtures.Component("FC_LAD_SimulateCylinder_Call", "FC", @"Blocks\FC_LAD_SimulateCylinder_Call [FC1].xml", "Program blocks/FC_LAD_SimulateCylinder_Call"),
            ManifestFixtures.Component("GlobalData", "DB", @"DB\GlobalData [DB1].xml", "Program blocks/GlobalData"));

        var result = ToolResults.OkJson(new KnowledgeTools().IngestSource(tree.Root, null));

        Assert.Equal("manifest", result.GetProperty("source").GetString());
        Assert.Equal(3, result.GetProperty("filesFound").GetInt32());
        Assert.Equal(3, result.GetProperty("filesImported").GetInt32());
        Assert.Equal(0, result.GetProperty("warnings").GetArrayLength());

        var graph = SqliteSemanticGraphStore.Load(result.GetProperty("dbPath").GetString()!);
        Assert.Equal(SemanticNodeKind.OrganizationBlock, graph.GetNode("block:Main").Kind);
        Assert.Equal(SemanticNodeKind.Function, graph.GetNode("block:FC_LAD_SimulateCylinder_Call").Kind);
        Assert.Equal(SemanticNodeKind.GlobalDataBlock, graph.GetNode("db:GlobalData").Kind);

        // Names, categories and paths come from the manifest, not the file system.
        Assert.Equal(@"Blocks\Main [OB1].xml", graph.GetNode("block:Main").Properties["sourceFile"]);
        Assert.Equal("Program blocks/Main", graph.GetNode("block:Main").Properties["folderPath"]);
        Assert.Equal(9, graph.FindNodesByKind(SemanticNodeKind.Network).Count);
        Assert.Equal(SemanticNodeKind.Instruction, graph.GetNode("instruction:Main:1:call:1").Kind);
        Assert.Equal(SemanticNodeKind.DataBlockMember, graph.GetNode("db-member:GlobalData:Config.MaxSpeed").Kind);

        var projectId = $"project:{Path.GetFileName(tree.Root)}";
        foreach (var target in new[] { "block:Main", "block:FC_LAD_SimulateCylinder_Call", "db:GlobalData" })
        {
            Assert.Contains(graph.Edges, edge =>
                edge.Type == SemanticRelationshipType.Contains &&
                edge.FromNodeId == projectId &&
                edge.ToNodeId == target);
        }
    }

    [Fact]
    public void ManifestModeWarnsOnMissingFileAndUnlistedXml()
    {
        using var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.MainObPath, Path.Combine("Blocks", "Main [OB1].xml"));
        // On disk but not referenced by the manifest (legacy flat layout / spike copies situation).
        tree.AddFixture(FixtureFiles.GlobalDataDbPath, "legacy [DB9].xml");
        ManifestFixtures.Write(tree,
            ManifestFixtures.Component("Main", "OB", @"Blocks\Main [OB1].xml", "Program blocks/Main"),
            ManifestFixtures.Component("GhostFc", "FC", @"Blocks\Ghost [FC9].xml", "Program blocks/GhostFc"));

        var result = ToolResults.OkJson(new KnowledgeTools().IngestSource(tree.Root, null));

        Assert.Equal("manifest", result.GetProperty("source").GetString());
        Assert.Equal(2, result.GetProperty("filesFound").GetInt32());
        Assert.Equal(1, result.GetProperty("filesImported").GetInt32());

        var warnings = result.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Contains(warnings, warning =>
            warning.Contains("marked Exported but its file is missing") && warning.Contains("GhostFc"));
        Assert.Contains(warnings, warning =>
            warning.Contains("not in manifest, ignored") && warning.Contains("legacy [DB9].xml"));

        var graph = SqliteSemanticGraphStore.Load(result.GetProperty("dbPath").GetString()!);
        Assert.Equal(SemanticNodeKind.OrganizationBlock, graph.GetNode("block:Main").Kind);
    }

    [Fact]
    public void ManifestModeImportsUdtAndTagEntries()
    {
        using var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.MainObPath, Path.Combine("Blocks", "Main [OB1].xml"));
        tree.AddText(Path.Combine("Types", "UDT_Motor.xml"),
            """<Document><SW.Types.PlcStruct ID="0"><AttributeList><Name>UDT_Motor</Name></AttributeList></SW.Types.PlcStruct></Document>""");
        tree.AddText(Path.Combine("Tags", "Default tag table.xml"),
            """<Document><SW.Tags.PlcTagTable ID="0"><AttributeList><Name>Default tag table</Name></AttributeList><ObjectList><SW.Tags.PlcTag ID="1" CompositionName="Tags"><AttributeList><DataTypeName>Bool</DataTypeName><LogicalAddress>%M0.0</LogicalAddress><Name>MotorRunning</Name></AttributeList></SW.Tags.PlcTag></ObjectList></SW.Tags.PlcTagTable></Document>""");
        ManifestFixtures.Write(tree,
            ManifestFixtures.Component("Main", "OB", @"Blocks\Main [OB1].xml", "Program blocks/Main"),
            ManifestFixtures.Component("UDT_Motor", "UDT", @"Types\UDT_Motor.xml", "PLC data types/UDT_Motor"),
            ManifestFixtures.Component("Default tag table", "Tags", @"Tags\Default tag table.xml", "PLC tags/Default tag table"));

        var result = ToolResults.OkJson(new KnowledgeTools().IngestSource(tree.Root, null));

        Assert.Equal(3, result.GetProperty("filesImported").GetInt32());
        Assert.Equal(0, result.GetProperty("warnings").GetArrayLength());

        var graph = SqliteSemanticGraphStore.Load(result.GetProperty("dbPath").GetString()!);
        Assert.Equal(SemanticNodeKind.UserDataType, graph.GetNode("udt:UDT_Motor").Kind);
        Assert.Equal(SemanticNodeKind.PlcTag, graph.GetNode("tag:Default tag table:MotorRunning:%M0.0").Kind);
        Assert.Equal(SemanticNodeKind.IoAddress, graph.GetNode("io:%M0.0").Kind);
    }

    [Fact]
    public void ManifestModeHandlesForwardSlashExportedFile()
    {
        using var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.MainObPath, Path.Combine("Blocks", "Main [OB1].xml"));
        ManifestFixtures.Write(tree,
            ManifestFixtures.Component("Main", "OB", "Blocks/Main [OB1].xml", "Program blocks/Main"));

        var result = ToolResults.OkJson(new KnowledgeTools().IngestSource(tree.Root, null));

        Assert.Equal(1, result.GetProperty("filesImported").GetInt32());
        Assert.Equal(0, result.GetProperty("warnings").GetArrayLength());
        var graph = SqliteSemanticGraphStore.Load(result.GetProperty("dbPath").GetString()!);
        Assert.Equal(
            Path.Combine("Blocks", "Main [OB1].xml"),
            graph.GetNode("block:Main").Properties["sourceFile"]);
    }

    [Fact]
    public void MalformedManifestReturnsManifestInvalid()
    {
        using var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.MainObPath, "Main [OB1].xml");
        tree.AddText("metadata.json", "{ this is not valid json");

        var error = ToolResults.ErrorJson(new KnowledgeTools().IngestSource(tree.Root, null));

        Assert.Equal("MANIFEST_INVALID", error.GetProperty("code").GetString());
    }

    [Fact]
    public void TreeWithoutManifestFallsBackToCrawl()
    {
        using var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.MainObPath, "Main [OB1].xml");

        var result = ToolResults.OkJson(new KnowledgeTools().IngestSource(tree.Root, null));

        Assert.Equal("crawl", result.GetProperty("source").GetString());
        Assert.Equal(1, result.GetProperty("filesImported").GetInt32());
    }

    [Fact]
    public void ManifestWithSyncExportFieldsStillImports()
    {
        // sync_export (mcp-engineering, 2026-07-20) extends the manifest with plcSoftwareChecksum,
        // contentHash and fingerprints while keeping schemaVersion "1.0" — the reader DTO must keep
        // ignoring unknown fields.
        using var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.MainObPath, Path.Combine("Blocks", "Main [OB1].xml"));
        tree.AddText("metadata.json", """
            {
              "schemaVersion": "1.0",
              "exportStartedUtc": "2026-07-20T08:00:00.0000000+00:00",
              "exportFinishedUtc": "2026-07-20T08:00:01.0000000+00:00",
              "exportRoot": "unused",
              "plcSoftwareChecksum": "4F 4F D9 C3 06 F9 C0 23",
              "components": [
                {
                  "id": "KTJyIUGV2W_2xDDF_u7qCPBXueQh_FdT564FtACYn70",
                  "name": "Main",
                  "sourcePath": "Main",
                  "category": "OB",
                  "folder": "Blocks",
                  "siemensTypeName": "OB",
                  "status": "Exported",
                  "exportedFile": "Blocks\\Main [OB1].xml",
                  "message": null,
                  "programmingLanguage": "LAD",
                  "tiaIdentifier": "Main",
                  "number": 1,
                  "isKnowHowProtected": false,
                  "creationDate": "2026-07-04T15:05:28.5729940+00:00",
                  "modifiedDate": "2026-07-18T15:37:54.7848587+00:00",
                  "codeModifiedDate": "2026-07-18T15:37:54.7848587+00:00",
                  "interfaceModifiedDate": "2008-07-21T16:55:08.4195470+00:00",
                  "contentHash": "ysxKe8N4Xrybb0d9yow7X5iawEgtEdgjKp8VEWnf4vg",
                  "fingerprints": "Code=9/y2tKG9dGm9rL2sqgQLz2sg6os=;Comments=H2i1+wMxWSK3ru6abY74v4KvJdQ="
                }
              ]
            }
            """);

        var result = ToolResults.OkJson(new KnowledgeTools().IngestSource(tree.Root, null));

        Assert.Equal("manifest", result.GetProperty("source").GetString());
        Assert.Equal(1, result.GetProperty("filesImported").GetInt32());
        Assert.Equal(0, result.GetProperty("warnings").GetArrayLength());
    }
}
