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
                <FlgNet><Parts><Access UId="1"><Symbol><Component Name="Enable" /></Symbol></Access><Access UId="2"><Symbol><Component Name="Delay" /></Symbol></Access><Part UId="3" Name="Contact" /><Part UId="4" Name="TON"><Instance UId="5"><Component Name="TimerDb" /></Instance></Part><Part UId="6" Name="Sr" /></Parts>
                <Wires>
                  <Wire><Powerrail /><NameCon UId="3" Name="in" /></Wire>
                  <Wire><IdentCon UId="1" /><NameCon UId="3" Name="operand" /></Wire>
                  <Wire><NameCon UId="3" Name="out" /><NameCon UId="4" Name="IN" /></Wire>
                  <Wire><IdentCon UId="2" /><NameCon UId="4" Name="PT" /></Wire>
                  <Wire><NameCon UId="4" Name="Q" /><NameCon UId="6" Name="s" /></Wire>
                </Wires></FlgNet>
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
        var ladder = network.Ladder!;
        var contact = Assert.Single(ladder.Elements, element => element.Id == "3");
        Assert.Equal("Enable", contact.Label);
        Assert.Equal("Enable", contact.ReferencedObject);
        Assert.Contains(contact.Pins, pin => pin.Name == "operand" && pin.Label == "Enable" && pin.ReferencedObject == "Enable");
        var timer = Assert.Single(ladder.Elements, element => element.Id == "4");
        Assert.Equal("TimerDb", timer.Label);
        Assert.Contains(timer.Pins, pin => pin.Name == "IN");
        Assert.Contains(timer.Pins, pin => pin.Name == "PT" && pin.Label == "Delay");
        Assert.Equal(5, ladder.Wires.Count);
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

    [Fact]
    public void ReadPreservesBranchedTimerAndSetResetPinsFromLadderXml()
    {
        var context = CreateContext();
        var file = Path.Combine(context.SourceRoot, "Blocks", "Cylinder.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """
            <Document><SW.Blocks.FC><AttributeList><Name>Cylinder</Name></AttributeList><SW.Blocks.CompileUnit ID="network-3"><AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList><FlgNet><Parts>
              <Access UId="21"><Symbol><Component Name="CylinderGoForwardPos" /></Symbol></Access><Access UId="22"><Symbol><Component Name="CylinderGoBackwardPos" /></Symbol></Access><Access UId="23"><Symbol><Component Name="CylinderMovementSimulate" /></Symbol></Access><Access Scope="LiteralConstant" UId="24"><Constant><ConstantType>Bool</ConstantType><ConstantValue>false</ConstantValue></Constant></Access><Access UId="25"><Symbol><Component Name="CylinderGoBackwardPos" /></Symbol></Access><Access UId="26"><Symbol><Component Name="io_Cylinder@ForwardPos" /></Symbol></Access>
              <Part Name="Contact" UId="27" /><Part Name="Contact" UId="28"><Negated Name="operand" /></Part><Part Name="TON" UId="29"><Instance UId="30"><Component Name="IEC_CylForwardMovement" /></Instance></Part><Part Name="Contact" UId="31" /><Part Name="Contact" UId="32" /><Part Name="Sr" UId="33" />
            </Parts><Wires>
              <Wire><Powerrail /><NameCon UId="27" Name="in" /><NameCon UId="32" Name="in" /></Wire><Wire><IdentCon UId="21" /><NameCon UId="27" Name="operand" /></Wire><Wire><NameCon UId="27" Name="out" /><NameCon UId="28" Name="in" /></Wire><Wire><IdentCon UId="22" /><NameCon UId="28" Name="operand" /></Wire><Wire><NameCon UId="28" Name="out" /><NameCon UId="29" Name="IN" /></Wire><Wire><IdentCon UId="23" /><NameCon UId="29" Name="PT" /></Wire><Wire><NameCon UId="29" Name="Q" /><NameCon UId="31" Name="in" /></Wire><Wire><NameCon UId="29" Name="ET" /><OpenCon UId="34" /></Wire><Wire><IdentCon UId="24" /><NameCon UId="31" Name="operand" /></Wire><Wire><NameCon UId="31" Name="out" /><NameCon UId="33" Name="s" /></Wire><Wire><IdentCon UId="25" /><NameCon UId="32" Name="operand" /></Wire><Wire><NameCon UId="32" Name="out" /><NameCon UId="33" Name="r1" /></Wire><Wire><IdentCon UId="26" /><NameCon UId="33" Name="operand" /></Wire>
            </Wires></FlgNet></SW.Blocks.CompileUnit></SW.Blocks.FC></Document>
            """);

        var ladder = Assert.Single(new SourceObjectInspectorReader().Read(context, "Blocks/Cylinder.xml", new DeviceSourceResolver(_ => { })).Networks).Ladder!;

        var timer = Assert.Single(ladder.Elements, element => element.Id == "29");
        Assert.Equal("IEC_CylForwardMovement", timer.Label);
        Assert.Contains(timer.Pins, pin => pin.Name == "PT" && pin.Label == "CylinderMovementSimulate");
        var falseContact = Assert.Single(ladder.Elements, element => element.Id == "31");
        Assert.Equal("false", falseContact.Label);
        Assert.Null(falseContact.ReferencedObject);
        Assert.Contains(falseContact.Pins, pin => pin.Name == "operand" && pin.Label == "false" && pin.ReferencedObject is null);
        var setReset = Assert.Single(ladder.Elements, element => element.Id == "33");
        Assert.Equal("io_Cylinder@ForwardPos", setReset.Label);
        Assert.Contains(setReset.Pins, pin => pin.Name == "s");
        Assert.Contains(setReset.Pins, pin => pin.Name == "r1");
        Assert.Equal(13, ladder.Wires.Count);
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
