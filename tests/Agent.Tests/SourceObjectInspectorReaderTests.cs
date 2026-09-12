using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class SourceObjectInspectorReaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "source-object-inspector-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReadProjectsBlockInterfaceAndLadderTopologyFromSourceXml()
    {
        var context = CreateContext();
        var file = Path.Combine(context.SourceRoot, "Blocks", "Main.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """
            <Document><SW.Blocks.FC ID="block"><AttributeList><Name>Main</Name></AttributeList>
              <Interface><Sections><Section Name="Input"><Member Name="Enable" Datatype="Bool" DefaultValue="false" /></Section></Sections></Interface>
              <SW.Blocks.CompileUnit ID="network-1"><AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>
                <FlgNet><Parts><Access UId="1"><Symbol><Component Name="Enable" /></Symbol></Access><Part UId="2" Name="Coil" /></Parts>
                <Wires><Wire><NameCon UId="1" Name="out" /><NameCon UId="2" Name="in" /></Wire></Wires></FlgNet>
              </SW.Blocks.CompileUnit>
            </SW.Blocks.FC></Document>
            """);

        var result = new SourceObjectInspectorReader().Read(context, "Blocks/Main.xml", new DeviceSourceResolver(_ => { }));

        Assert.Equal("Blocks", result.Category);
        Assert.Equal("Main", result.Name);
        var member = Assert.Single(Assert.Single(result.Interfaces).Members);
        Assert.Equal("Enable", member.Name);
        Assert.Equal("Bool", member.DataType);
        var network = Assert.Single(result.Networks);
        Assert.Equal("LAD", network.Language);
        Assert.NotNull(network.Ladder);
        var ladderElement = Assert.Single(network.Ladder!.Elements);
        Assert.Equal("Enable", ladderElement.Label);
        Assert.Equal("Enable", ladderElement.ReferencedObject);
        Assert.Single(network.Ladder.Wires);
    }

    [Fact]
    public void ReadProjectsTagTableRowsFromSourceXml()
    {
        var context = CreateContext();
        var file = Path.Combine(context.SourceRoot, "Tags", "Signals.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """
            <Document><SW.Tags.PlcTagTable><AttributeList><Name>Signals</Name></AttributeList><ObjectList>
              <SW.Tags.PlcTag><AttributeList><Name>MotorRun</Name><DataTypeName>Bool</DataTypeName><LogicalAddress>%Q0.0</LogicalAddress></AttributeList></SW.Tags.PlcTag>
            </ObjectList></SW.Tags.PlcTagTable></Document>
            """);

        var result = new SourceObjectInspectorReader().Read(context, "Tags/Signals.xml", new DeviceSourceResolver(_ => { }));

        Assert.Equal("Tags", result.Category);
        var row = Assert.Single(Assert.Single(result.Tables).Rows);
        Assert.Equal("MotorRun", row["Name"]);
        Assert.Equal("Bool", row["Data type"]);
        Assert.Equal("%Q0.0", row["Address"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private DeviceContext CreateContext()
    {
        var worktree = Path.Combine(root, "worktree");
        var device = Path.Combine(worktree, "devices", "PLC_1");
        return new DeviceContext("wb", "wt", "device", root, worktree, device, Path.Combine(device, "source"), Path.Combine(device, "staging"), Path.Combine(device, "plc-knowledge.db"));
    }
}
