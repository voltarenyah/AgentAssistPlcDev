using System.Text;
using System.Linq;
using System.Collections.Generic;
using Xunit;

namespace PlcXml.Model.Tests;

public sealed class PlcTypedPayloadTests
{
    [Fact]
    public void Checked_in_interface_ladder_and_structured_text_fixtures_dispatch_to_typed_payloads()
    {
        var main = PlcXmlParser.Parse(FixtureFiles.ReadAllBytes(FixtureFiles.MainObPath));
        var ladder = PlcXmlParser.Parse(FixtureFiles.ReadAllBytes(FixtureFiles.SimulateCylinderFcPath));
        var scl = PlcXmlParser.Parse(FixtureFiles.ReadAllBytes(FixtureFiles.SclAssignFcPath));

        Assert.Contains(AllAttributes(main), a => a.Payload is PlcInterface);
        Assert.Contains(AllAttributes(ladder), a => a.Payload is LadderNetwork);
        Assert.Contains(AllAttributes(scl), a => a.Payload is StructuredTextNetwork);
    }

    [Fact]
    public void Supported_fixture_shapes_expose_interface_ladder_and_structured_text_payloads()
    {
        var interfaceDocument = Parse("<Document><SW.Blocks.OB><AttributeList><Interface><Sections xmlns=\"http://www.siemens.com/automation/Openness/SW/Interface/v5\"><Section Name=\"Input\"><Member Name=\"Start\" Datatype=\"Bool\" /></Section></Sections></Interface></AttributeList></SW.Blocks.OB></Document>");
        var plcInterface = Assert.IsType<PlcInterface>(Assert.Single(Assert.Single(interfaceDocument.Objects).Attributes).Payload);
        Assert.Equal("Input", Assert.Single(plcInterface.Sections).Name);
        Assert.Equal("Start", Assert.Single(Assert.Single(plcInterface.Sections).Members).Name);

        var ladderDocument = Parse("<Document><SW.Blocks.OB><AttributeList><NetworkSource><FlgNet xmlns=\"http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4\"><Parts><Access Scope=\"LocalVariable\" /><Part Name=\"Contact\" /></Parts><Wires><Powerrail /><NameCon /></Wires></FlgNet></NetworkSource></AttributeList></SW.Blocks.OB></Document>");
        var ladder = Assert.IsType<LadderNetwork>(Assert.Single(Assert.Single(ladderDocument.Objects).Attributes).Payload);
        Assert.Single(ladder.Accesses); Assert.Single(ladder.Parts); Assert.Equal(new[] { "Powerrail", "NameCon" }, ladder.Wires.Select(w => w.Name));

        var structuredDocument = Parse("<Document><SW.Blocks.OB><AttributeList><NetworkSource><StructuredText xmlns=\"http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v3\"><Token Text=\"IF\" /><Blank Num=\"1\" /><NewLine Num=\"1\" /><Access Scope=\"GlobalVariable\" /></StructuredText></NetworkSource></AttributeList></SW.Blocks.OB></Document>");
        var structured = Assert.IsType<StructuredTextNetwork>(Assert.Single(Assert.Single(structuredDocument.Objects).Attributes).Payload);
        Assert.Collection(structured.Entries, e => Assert.IsType<StToken>(e), e => Assert.IsType<StBlank>(e), e => Assert.IsType<StNewLine>(e), e => Assert.IsType<StAccess>(e));
    }

    [Fact]
    public void Unsupported_shapes_remain_generic_raw_and_unknown_children_are_ordered_raw()
    {
        var document = Parse("<Document><SW.Blocks.OB><AttributeList><Interface><Sections xmlns=\"urn:wrong\" /></Interface><NetworkSource><FlgNet xmlns=\"http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5\" /></NetworkSource><NetworkSource><StructuredText xmlns=\"http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v2\" /></NetworkSource></AttributeList></SW.Blocks.OB></Document>");
        var attributes = Assert.Single(document.Objects).Attributes;
        Assert.All(attributes, attribute => Assert.Null(attribute.Payload));

        var typed = Parse("<Document><SW.Blocks.OB><AttributeList><NetworkSource><StructuredText xmlns=\"http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v3\"><Token Text=\"x\" /><Vendor /><Blank Num=\"1\" /></StructuredText></NetworkSource></AttributeList></SW.Blocks.OB></Document>");
        var entries = Assert.IsType<StructuredTextNetwork>(Assert.Single(Assert.Single(typed.Objects).Attributes).Payload).Entries;
        Assert.Collection(entries, e => Assert.IsType<StToken>(e), e => Assert.IsType<StRaw>(e), e => Assert.IsType<StBlank>(e));
    }

    [Fact]
    public void Supported_payload_namespace_in_an_unsupported_wrapper_remains_raw()
    {
        var document = Parse("<Document><SW.Blocks.OB><AttributeList><OtherSource><FlgNet xmlns=\"http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4\" /></OtherSource></AttributeList></SW.Blocks.OB></Document>");

        Assert.Null(Assert.Single(Assert.Single(document.Objects).Attributes).Payload);
    }

    private static PlcDocument Parse(string xml) => PlcXmlParser.Parse(Encoding.UTF8.GetBytes(xml));
    private static IEnumerable<PlcAttribute> AllAttributes(PlcDocument document) =>
        document.Objects.SelectMany(AllObjects).SelectMany(o => o.Attributes);
    private static IEnumerable<PlcObject> AllObjects(PlcObject value) =>
        new[] { value }.Concat(value.Compositions.SelectMany(AllObjects));
}
