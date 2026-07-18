// Parser tests for the ported ProgramSemanticReferenceBuilder.
// ParsesRealFcFixture*/ParsesRealObFixture* assert what the real V17 exports in Fixtures/ actually contain;
// the other two are ported from PlcSourceExporter (tests/PlcSourceExporter.Core.Tests/ProgramSemanticReferenceTests.cs)
// and cover behaviours the real fixtures do not exercise (SCL call syntax, title culture preference).
using System;
using System.Linq;
using Mcp.Knowledge.Parsing;
using Xunit;

namespace Mcp.Knowledge.Tests;

public sealed class ProgramSemanticReferenceTests
{
    private static readonly ProgramBlockComponent FcComponent = new(
        "FC_LAD_SimulateCylinder_Call",
        "FC",
        "Program blocks/FC_LAD_SimulateCylinder_Call",
        "FC_LAD_SimulateCylinder_Call [FC1].xml");

    private static readonly ProgramBlockComponent ObComponent = new(
        "Main",
        "OB",
        "Program blocks/Main",
        "Main [OB1].xml");

    [Fact]
    public void ParsesRealFcFixtureNetworksAndFallbackTitles()
    {
        var result = ProgramSemanticReferenceBuilder.Parse(FixtureFiles.ReadAllText(FixtureFiles.SimulateCylinderFcPath), FcComponent);

        // The export has 7 SW.Blocks.CompileUnit elements (IDs 3, 8, D, 12, 17, 1C, 21); all titles are empty.
        Assert.Equal(7, result.Networks.Count);
        Assert.Equal(
            new[] { "Network 1", "Network 2", "Network 3", "Network 4", "Network 5", "Network 6", "Network 7" },
            result.Networks.Select(network => network.Title).ToArray());
        Assert.All(result.Networks, network => Assert.Equal("LAD", network.Language));
        Assert.All(result.Networks, network => Assert.Equal("FC", network.BlockKind));
        Assert.Equal("3", result.Networks[0].CompileUnitId);
        Assert.Equal("21", result.Networks[6].CompileUnitId);
        Assert.Equal(
            Enumerable.Range(1, 7).Select(index => $"network:FC_LAD_SimulateCylinder_Call:{index}").ToArray(),
            result.Networks.Select(network => network.Id).ToArray());
    }

    [Fact]
    public void ParsesRealFcFixtureReadsAndWrites()
    {
        var result = ProgramSemanticReferenceBuilder.Parse(FixtureFiles.ReadAllText(FixtureFiles.SimulateCylinderFcPath), FcComponent);

        // Network 1: negated contacts on io_Cylinder@ForwardPos / CylinderGoForwardPos, contact on btn_forward,
        // RCoil on outputGoBackwardPos, SCoil on outputGoForwardPos.
        var network1 = result.Networks[0];
        Assert.Equal(new[] { "btn_forward", "CylinderGoForwardPos", "io_Cylinder@ForwardPos" }, network1.Reads);
        Assert.Equal(new[] { "outputGoBackwardPos", "outputGoForwardPos" }, network1.Writes);
        Assert.Empty(network1.Calls);

        // Network 3: TON forward timer — PT read, Sr operand write.
        var network3 = result.Networks[2];
        Assert.Equal(new[] { "CylinderGoBackwardPos", "CylinderGoForwardPos", "CylinderMovementSimulate" }, network3.Reads);
        Assert.Equal(new[] { "io_Cylinder@ForwardPos" }, network3.Writes);

        // Network 5: plain coil latching the moving flag.
        var network5 = result.Networks[4];
        Assert.Equal(new[] { "Cylinder@ForwardPos", "CylinderGoForwardPos" }, network5.Reads);
        Assert.Equal(new[] { "CylinderIsMovingForward" }, network5.Writes);

        // Network 7 has an empty NetworkSource: no references at all.
        Assert.Empty(result.Networks[6].Reads);
        Assert.Empty(result.Networks[6].Writes);

        // This FC calls nothing; every access is classifiable, so no "unknown" references remain.
        Assert.All(result.Networks, network => Assert.Empty(network.Calls));
        Assert.DoesNotContain(result.References, reference => reference.Access == "call");
        Assert.DoesNotContain(result.References, reference => reference.Access == "unknown");
        Assert.Contains(result.References, reference =>
            reference.To == "CylinderGoForwardPos" &&
            reference.Scope == "GlobalVariable" &&
            reference.Access == "read");
        Assert.Contains(result.References, reference =>
            reference.To == "CylinderIsMovingForward" &&
            reference.Access == "write" &&
            reference.NetworkIndex == 5);
    }

    [Fact]
    public void ParsesRealObFixtureCallNetwork()
    {
        var result = ProgramSemanticReferenceBuilder.Parse(FixtureFiles.ReadAllText(FixtureFiles.MainObPath), ObComponent);

        // Two compile units: network 1 is the FC call, network 2 is empty.
        Assert.Equal(2, result.Networks.Count);
        Assert.Equal("Network 1", result.Networks[0].Title);
        Assert.Equal("Network 2", result.Networks[1].Title);

        var network1 = result.Networks[0];
        Assert.Equal(new[] { "FC_LAD_SimulateCylinder_Call" }, network1.Calls);
        Assert.Equal(new[] { "Btn_BackwardCommand", "Btn_ForwardCommand" }, network1.Reads);
        Assert.Equal(new[] { "CylinderGoBackwardPos", "CylinderGoForwardPos" }, network1.Writes);

        // The block call itself.
        Assert.Contains(result.References, reference =>
            reference.TargetKind == "block" &&
            reference.Access == "call" &&
            reference.To == "FC_LAD_SimulateCylinder_Call" &&
            reference.CalleeBlockType == "FC" &&
            reference.From == "network:Main:1");

        // Parameter bindings classified by section: Input → read, Output → write, InOut → inout.
        Assert.Contains(result.References, reference =>
            reference.Access == "read" &&
            reference.To == "Btn_ForwardCommand" &&
            reference.Parameter == "btn_forward");
        Assert.Contains(result.References, reference =>
            reference.Access == "write" &&
            reference.To == "CylinderGoForwardPos" &&
            reference.Parameter == "outputGoForwardPos");
        Assert.Contains(result.References, reference =>
            reference.Access == "inout" &&
            reference.To == "Cylinder@ForwardPos" &&
            reference.Parameter == "io_Cylinder@ForwardPos");

        // The empty second network contributes no references.
        Assert.DoesNotContain(result.References, reference => reference.From == "network:Main:2");
    }

    [Fact]
    public void ParsesSclAccessCallInfoWithoutNameAttribute()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.FB ID="0">
                <AttributeList>
                  <Name>Cell_Sequence</Name>
                  <ProgrammingLanguage>SCL</ProgrammingLanguage>
                </AttributeList>
                <ObjectList>
                  <SW.Blocks.CompileUnit ID="10" CompositionName="CompileUnits">
                    <AttributeList>
                      <NetworkSource><StructuredText xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4">
                        <Access Scope="GlobalVariable" UId="50">
                          <CallInfo UId="57" BlockType="FC">
                            <Instance Scope="GlobalVariable" UId="59">
                              <Component Name="FC_ArrayUintHandling" UId="58" />
                            </Instance>
                            <Parameter Name="I_InNumber" Section="Input" UId="61">
                              <Access Scope="GlobalVariable" UId="65"><Symbol><Component Name="Cell_DB" /><Component Name="Count" /></Symbol></Access>
                            </Parameter>
                            <Parameter Name="O_Result" Section="Output" UId="72">
                              <Access Scope="GlobalVariable" UId="76"><Symbol><Component Name="Cell_DB" /><Component Name="Result" /></Symbol></Access>
                            </Parameter>
                          </CallInfo>
                        </Access>
                      </StructuredText></NetworkSource>
                      <ProgrammingLanguage>SCL</ProgrammingLanguage>
                    </AttributeList>
                  </SW.Blocks.CompileUnit>
                </ObjectList>
              </SW.Blocks.FB>
            </Document>
            """;

        var result = ProgramSemanticReferenceBuilder.Parse(
            xml,
            new ProgramBlockComponent("Cell_Sequence", "FB", "Blocks/Cell_Sequence", "Blocks\\Cell_Sequence.xml"));

        var network = Assert.Single(result.Networks);
        Assert.Equal("SCL", network.Language);
        Assert.Contains("FC_ArrayUintHandling", network.Calls);
        Assert.Contains("Cell_DB.Count", network.Reads);
        Assert.Contains("Cell_DB.Result", network.Writes);
        Assert.Contains(result.References, item => item.Access == "call" && item.To == "FC_ArrayUintHandling");
        Assert.Contains(result.References, item => item.Access == "read" && item.To == "Cell_DB.Count" && item.Parameter == "I_InNumber");
        Assert.Contains(result.References, item => item.Access == "write" && item.To == "Cell_DB.Result" && item.Parameter == "O_Result");
    }

    [Fact]
    public void FallsBackNetworkTitlesDeterministically()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <SW.Blocks.FC ID="0">
                <AttributeList><Name>Fallbacks</Name><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>
                <ObjectList>
                  <SW.Blocks.CompileUnit ID="1"><AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList><ObjectList>
                    <MultilingualText CompositionName="Title"><ObjectList>
                      <MultilingualTextItem><AttributeList><Culture>en-US</Culture><Text>US title</Text></AttributeList></MultilingualTextItem>
                      <MultilingualTextItem><AttributeList><Culture>en-GB</Culture><Text>GB title</Text></AttributeList></MultilingualTextItem>
                    </ObjectList></MultilingualText>
                  </ObjectList></SW.Blocks.CompileUnit>
                  <SW.Blocks.CompileUnit ID="2"><AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList><ObjectList>
                    <MultilingualText CompositionName="Title"><ObjectList>
                      <MultilingualTextItem><AttributeList><Culture>zh-CN</Culture><Text>First non-empty</Text></AttributeList></MultilingualTextItem>
                    </ObjectList></MultilingualText>
                  </ObjectList></SW.Blocks.CompileUnit>
                  <SW.Blocks.CompileUnit ID="3"><AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList></SW.Blocks.CompileUnit>
                </ObjectList>
              </SW.Blocks.FC>
            </Document>
            """;

        var result = ProgramSemanticReferenceBuilder.Parse(
            xml,
            new ProgramBlockComponent("Fallbacks", "FC", "Blocks/Fallbacks", "Blocks\\Fallbacks.xml"));

        Assert.Collection(
            result.Networks,
            item => Assert.Equal("GB title", item.Title),
            item => Assert.Equal("First non-empty", item.Title),
            item => Assert.Equal("Network 3", item.Title));
    }
}
