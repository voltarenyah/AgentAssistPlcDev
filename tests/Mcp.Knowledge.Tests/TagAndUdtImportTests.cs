using System.IO;
using System.Linq;
using Mcp.Knowledge.Graph;
using Mcp.Knowledge.Parsing;
using Mcp.Knowledge.Tools;
using Xunit;

namespace Mcp.Knowledge.Tests;

public sealed class TagAndUdtImportTests
{
    [Fact]
    public void ParsesRealTagTableFixture()
    {
        var rows = TagTableBuilder.ParseRows(
            FixtureFiles.ReadAllText(FixtureFiles.IoCcTagTablePath),
            "IO_CC_Cav_A.xml",
            "PLC tags/IO_CC_Cav_A");

        Assert.Equal(10, rows.Count);
        Assert.All(rows, row => Assert.Equal("IO_CC_Cav_A", row.TagTable));
        Assert.All(rows, row => Assert.Equal("Bool", row.DataType));

        var first = rows[0];
        Assert.Equal("O_CC_Coupelle_Up_Cav_A", first.Name);
        Assert.Equal("%Q600.7", first.LogicalAddress);
        Assert.Equal("tag:IO_CC_Cav_A:O_CC_Coupelle_Up_Cav_A:%Q600.7", first.Id);
        Assert.True(first.ExternalAccessible);
        Assert.True(first.ExternalVisible);
        Assert.True(first.ExternalWritable);
        Assert.Contains(rows, row => row.Name == "I_CC_TyrePresent_Pos_1_Cav_A" && row.LogicalAddress == "%I601.0");
        Assert.Contains(rows, row => row.Name == "O_CC_Coupelle_Down_Cav_A" && row.LogicalAddress == "%Q601.7");
    }

    [Fact]
    public void ImportsRealTagTableFixtureIntoGraph()
    {
        var graph = new SemanticPlcGraph();
        TiaXmlSemanticGraphImporter.ImportTagTableXml(
            FixtureFiles.ReadAllText(FixtureFiles.IoCcTagTablePath),
            "IO_CC_Cav_A.xml",
            "PLC tags/IO_CC_Cav_A",
            graph);

        var tag = graph.GetNode("tag:IO_CC_Cav_A:O_CC_Coupelle_Up_Cav_A:%Q600.7");
        Assert.Equal(SemanticNodeKind.PlcTag, tag.Kind);
        Assert.Equal("IO_CC_Cav_A", tag.Properties["tagTable"]);
        Assert.Equal("%Q600.7", tag.Properties["logicalAddress"]);
        Assert.Equal("PLC tags/IO_CC_Cav_A", tag.Properties["folderPath"]);

        Assert.Equal(10, graph.FindNodesByKind(SemanticNodeKind.PlcTag).Count);
        Assert.Equal(10, graph.FindNodesByKind(SemanticNodeKind.IoAddress).Count);
        Assert.Equal(SemanticNodeKind.IoAddress, graph.GetNode("io:%Q600.7").Kind);
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.ConnectedTo &&
            edge.FromNodeId == tag.Id &&
            edge.ToNodeId == "io:%Q600.7");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.HasType &&
            edge.FromNodeId == tag.Id &&
            edge.ToNodeId == "type:Bool");
        Assert.Equal(10, graph.Edges.Count(edge => edge.Type == SemanticRelationshipType.ConnectedTo));
    }

    [Fact]
    public void ParsesQuotedUdtDataTypeAndComment()
    {
        // Ported from PlcSourceExporter TagTableTests.ParsesTagTableXmlIntoFlatTagRows.
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Tags.PlcTagTable ID="0">
                <AttributeList>
                  <Name>Robot IO</Name>
                </AttributeList>
                <ObjectList>
                  <SW.Tags.PlcTag ID="1" CompositionName="Tags">
                    <AttributeList>
                      <DataTypeName>&quot;UDT_Robot_In&quot;</DataTypeName>
                      <ExternalAccessible>true</ExternalAccessible>
                      <ExternalVisible>false</ExternalVisible>
                      <ExternalWritable>true</ExternalWritable>
                      <LogicalAddress>%I7000.0</LogicalAddress>
                      <Name>Robot_IN</Name>
                    </AttributeList>
                    <ObjectList>
                      <MultilingualText ID="2" CompositionName="Comment">
                        <ObjectList>
                          <MultilingualTextItem ID="3" CompositionName="Items">
                            <AttributeList>
                              <Culture>en-US</Culture>
                              <Text>Robot input area</Text>
                            </AttributeList>
                          </MultilingualTextItem>
                        </ObjectList>
                      </MultilingualText>
                    </ObjectList>
                  </SW.Tags.PlcTag>
                  <SW.Tags.PlcTag ID="4" CompositionName="Tags">
                    <AttributeList>
                      <DataTypeName>Bool</DataTypeName>
                      <LogicalAddress>%M1.0</LogicalAddress>
                      <Name>Ready</Name>
                    </AttributeList>
                  </SW.Tags.PlcTag>
                </ObjectList>
              </SW.Tags.PlcTagTable>
            </Document>
            """;

        var rows = TagTableBuilder.ParseRows(xml, "Tags\\Robot IO.xml", "Tags/Robot IO");

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("tag:Robot IO:Robot_IN:%I7000.0", row.Id);
                Assert.Equal("Robot IO", row.TagTable);
                Assert.Equal("Tags/Robot IO", row.TagTableSourcePath);
                Assert.Equal("Robot_IN", row.Name);
                Assert.Equal("UDT_Robot_In", row.DataType);
                Assert.Equal("\"UDT_Robot_In\"", row.RawDataType);
                Assert.Equal("%I7000.0", row.LogicalAddress);
                Assert.True(row.ExternalAccessible);
                Assert.False(row.ExternalVisible);
                Assert.True(row.ExternalWritable);
                Assert.Equal("Robot input area", row.Comment);
                Assert.Equal("Tags\\Robot IO.xml", row.SourceFile);
            },
            row =>
            {
                Assert.Equal("Ready", row.Name);
                Assert.Equal("Bool", row.DataType);
                Assert.Null(row.ExternalAccessible);
                Assert.Null(row.ExternalVisible);
                Assert.Null(row.ExternalWritable);
            });
    }

    [Fact]
    public void ParsesRealUdtFixture()
    {
        var rows = UdtTypeTableBuilder.ParseRows(
            FixtureFiles.ReadAllText(FixtureFiles.CabUdtPath),
            "CAB.xml",
            "PLC data types/CAB");

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("Type", row.Kind);
                Assert.Equal("CAB", row.Name);
                Assert.Equal("CAB", row.DataType);
            },
            row =>
            {
                Assert.Equal("Member", row.Kind);
                Assert.Equal("CAB", row.ParentType);
                Assert.Equal("CAB", row.Name);
                Assert.Equal("CAB", row.Path);
                Assert.Equal("String[20]", row.DataType);
            });
    }

    [Fact]
    public void ImportsRealUdtFixtureIntoGraph()
    {
        var graph = new SemanticPlcGraph();
        TiaXmlSemanticGraphImporter.ImportUdtXml(
            FixtureFiles.ReadAllText(FixtureFiles.CabUdtPath),
            "CAB.xml",
            "PLC data types/CAB",
            graph);

        var udt = graph.GetNode("udt:CAB");
        Assert.Equal(SemanticNodeKind.UserDataType, udt.Kind);
        Assert.Equal("CAB.xml", udt.Properties["sourceFile"]);

        var member = graph.GetNode("udt-member:CAB:CAB");
        Assert.Equal(SemanticNodeKind.UserDataTypeMember, member.Kind);
        Assert.Equal("CAB", member.Properties["path"]);

        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Contains &&
            edge.FromNodeId == "udt:CAB" &&
            edge.ToNodeId == "udt-member:CAB:CAB");
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.HasType &&
            edge.FromNodeId == "udt-member:CAB:CAB" &&
            edge.ToNodeId == "type:String[20]");
    }

    [Fact]
    public void ParsesOnlyFirstLevelUdtMembersIntoTheTypeTable()
    {
        // Ported from PlcSourceExporter UdtTypeTableTests — nested struct members are intentionally
        // NOT flattened by the reference builder.
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <Engineering version="V20" />
              <SW.Types.PlcStruct ID="0">
                <AttributeList>
                  <Interface><Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
                    <Section Name="None">
                      <Member Name="Ready" Datatype="Bool" />
                      <Member Name="Motion" Datatype="UDT_Motion">
                        <Sections>
                          <Section Name="None">
                            <Member Name="Command" Datatype="UDT_MotionCommand" />
                            <Member Name="Speed" Datatype="Real" />
                          </Section>
                        </Sections>
                      </Member>
                    </Section>
                  </Sections></Interface>
                  <Name>UDT_Cell</Name>
                </AttributeList>
              </SW.Types.PlcStruct>
            </Document>
            """;

        var rows = UdtTypeTableBuilder.ParseRows(xml, "UDT\\UDT_Cell.xml", "Types/UDT_Cell");

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("Type", row.Kind);
                Assert.Equal("", row.ParentType);
                Assert.Equal("UDT_Cell", row.Name);
                Assert.Equal("UDT_Cell", row.DataType);
                Assert.Equal("", row.Path);
            },
            row =>
            {
                Assert.Equal("Member", row.Kind);
                Assert.Equal("UDT_Cell", row.ParentType);
                Assert.Equal("", row.ParentPath);
                Assert.Equal("Ready", row.Name);
                Assert.Equal("Ready", row.Path);
                Assert.Equal("Bool", row.DataType);
            },
            row =>
            {
                Assert.Equal("Member", row.Kind);
                Assert.Equal("UDT_Cell", row.ParentType);
                Assert.Equal("", row.ParentPath);
                Assert.Equal("Motion", row.Name);
                Assert.Equal("Motion", row.Path);
                Assert.Equal("UDT_Motion", row.DataType);
            });
    }

    [Fact]
    public void ManifestModeImportsTagsAndUdtEntries()
    {
        using var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.MainObPath, Path.Combine("Blocks", "Main [OB1].xml"));
        tree.AddFixture(FixtureFiles.IoCcTagTablePath, Path.Combine("Tags", "IO_CC_Cav_A.xml"));
        tree.AddFixture(FixtureFiles.CabUdtPath, Path.Combine("UDT", "CAB.xml"));
        ManifestFixtures.Write(tree,
            ManifestFixtures.Component("Main", "OB", @"Blocks\Main [OB1].xml", "Program blocks/Main"),
            ManifestFixtures.Component("IO_CC_Cav_A", "Tags", @"Tags\IO_CC_Cav_A.xml", "PLC tags/IO_CC_Cav_A"),
            ManifestFixtures.Component("CAB", "UDT", @"UDT\CAB.xml", "PLC data types/CAB"));

        var result = ToolResults.OkJson(new KnowledgeTools().IngestSource(tree.Root, null));

        Assert.Equal("manifest", result.GetProperty("source").GetString());
        Assert.Equal(3, result.GetProperty("filesImported").GetInt32());
        Assert.Equal(0, result.GetProperty("warnings").GetArrayLength());

        var graph = SqliteSemanticGraphStore.Load(result.GetProperty("dbPath").GetString()!);
        Assert.Equal(10, graph.FindNodesByKind(SemanticNodeKind.PlcTag).Count);
        Assert.Equal(SemanticNodeKind.UserDataType, graph.GetNode("udt:CAB").Kind);

        var projectId = $"project:{Path.GetFileName(tree.Root)}";
        Assert.Contains(graph.Edges, edge =>
            edge.Type == SemanticRelationshipType.Contains &&
            edge.FromNodeId == projectId &&
            edge.ToNodeId == "udt:CAB");
        // Reference behaviour: tag tables get no project CONTAINS edge.
        Assert.DoesNotContain(graph.Edges, edge =>
            edge.FromNodeId == projectId && edge.ToNodeId.StartsWith("tag:"));
    }

    [Fact]
    public void CrawlModeImportsTagsAndUdtFiles()
    {
        using var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.IoCcTagTablePath, Path.Combine("Tags", "IO_CC_Cav_A.xml"));
        tree.AddFixture(FixtureFiles.CabUdtPath, Path.Combine("UDT", "CAB.xml"));

        var result = ToolResults.OkJson(new KnowledgeTools().IngestSource(tree.Root, null));

        Assert.Equal("crawl", result.GetProperty("source").GetString());
        Assert.Equal(2, result.GetProperty("filesImported").GetInt32());
        Assert.Equal(0, result.GetProperty("warnings").GetArrayLength());

        var graph = SqliteSemanticGraphStore.Load(result.GetProperty("dbPath").GetString()!);
        Assert.Equal(10, graph.FindNodesByKind(SemanticNodeKind.PlcTag).Count);
        Assert.Equal(SemanticNodeKind.UserDataType, graph.GetNode("udt:CAB").Kind);
    }

    [Fact]
    public void FullTreeIngestIsDeterministic()
    {
        using var tree = new TempExportTree();
        tree.AddFixture(FixtureFiles.MainObPath, "Main [OB1].xml");
        tree.AddFixture(FixtureFiles.SimulateCylinderFcPath, "FC_LAD_SimulateCylinder_Call [FC1].xml");
        tree.AddFixture(FixtureFiles.GlobalDataDbPath, "GlobalData [DB1].xml");
        tree.AddFixture(FixtureFiles.MotorFbInstanceDbPath, "MotorFbInstance [DB2].xml");
        tree.AddFixture(FixtureFiles.IoCcTagTablePath, Path.Combine("Tags", "IO_CC_Cav_A.xml"));
        tree.AddFixture(FixtureFiles.CabUdtPath, Path.Combine("UDT", "CAB.xml"));

        var tools = new KnowledgeTools();
        var firstDb = Path.Combine(tree.Root, "first.db");
        var secondDb = Path.Combine(tree.Root, "second.db");
        var firstResult = ToolResults.OkJson(tools.IngestSource(tree.Root, firstDb));
        ToolResults.OkJson(tools.IngestSource(tree.Root, secondDb));

        Assert.Equal(6, firstResult.GetProperty("filesImported").GetInt32());
        Assert.Equal(0, firstResult.GetProperty("warnings").GetArrayLength());

        var first = SqliteSemanticGraphStore.Load(firstDb);
        var second = SqliteSemanticGraphStore.Load(secondDb);
        Assert.Equal(first.Nodes.Select(node => node.Id).ToArray(), second.Nodes.Select(node => node.Id).ToArray());
        Assert.Equal(first.Edges.Select(edge => edge.Id).ToArray(), second.Edges.Select(edge => edge.Id).ToArray());

        Assert.Equal(65, first.Nodes.Count);
        Assert.Equal(10, first.Edges.Count(edge => edge.Type == SemanticRelationshipType.ConnectedTo));
        Assert.Equal(10, first.FindNodesByKind(SemanticNodeKind.PlcTag).Count);
        Assert.Equal(10, first.FindNodesByKind(SemanticNodeKind.IoAddress).Count);
        Assert.Equal(1, first.FindNodesByKind(SemanticNodeKind.UserDataType).Count);
    }
}
