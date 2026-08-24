using System.Text;
using Xunit;

namespace PlcXml.Model.Tests;

public sealed class PlcDiagnosticsTests
{
    [Fact]
    public void Malformed_xml_reports_parse_code_and_source_without_a_document()
    {
        var input = Encoding.UTF8.GetBytes("<Document><SW.Blocks.OB>");

        var error = Assert.Throws<PlcXmlParseException>(() => PlcXmlParser.Parse(input, "bad.xml"));

        Assert.Equal("PLCXML_PARSE_INVALID", error.Code);
        Assert.Equal("bad.xml", error.SourceName);
        Assert.Null(error.Location);
    }

    [Fact]
    public void Unsupported_root_reports_root_code_and_source_without_a_document()
    {
        var error = Assert.Throws<PlcXmlParseException>(() => PlcXmlParser.Parse(
            Encoding.UTF8.GetBytes("<NotAPlcDocument />"), "root.xml"));

        Assert.Equal("PLCXML_ROOT_UNSUPPORTED", error.Code);
        Assert.Equal("root.xml", error.SourceName);
        Assert.Null(error.Location);
    }

    [Fact]
    public void Malformed_supported_payload_reports_payload_code_and_model_location()
    {
        var input = Encoding.UTF8.GetBytes("<Document><SW.Blocks.OB ID=\"OB1\"><AttributeList><NetworkSource><FlgNet xmlns=\"http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4\" /><Extra /></NetworkSource></AttributeList></SW.Blocks.OB></Document>");

        var error = Assert.Throws<PlcXmlModelException>(() => PlcXmlParser.Parse(input, "payload.xml"));

        Assert.Equal("PLCXML_PAYLOAD_INVALID", error.Code);
        Assert.Equal("payload.xml", error.SourceName);
        Assert.Contains("/Document/SW.Blocks.OB[0]", error.Location!.Path);
    }

    [Fact]
    public void Invalid_mutation_arguments_report_code_and_network_location_without_output()
    {
        var document = Parse("<Document><SW.Blocks.OB><ObjectList><SW.Blocks.CompileUnit ID=\"N1\" /></ObjectList></SW.Blocks.OB></Document>", "mutation.xml");
        var network = Assert.Single(document.Networks);

        var error = Assert.Throws<PlcXmlModelException>(() => network.SetTitleText("", "replacement"));

        Assert.Equal("PLCXML_MUTATION_INVALID", error.Code);
        Assert.Equal("mutation.xml", error.SourceName);
        Assert.Equal(network.Location.Path, error.Location!.Path);
    }

    [Fact]
    public void Missing_and_ambiguous_text_targets_report_exact_codes_and_location()
    {
        var missing = Parse("<Document><SW.Blocks.OB><ObjectList><SW.Blocks.CompileUnit ID=\"N1\"><ObjectList><MultilingualText CompositionName=\"Title\"><ObjectList><MultilingualTextItem><AttributeList><Culture>de-DE</Culture><Text>x</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.OB></Document>", "missing.xml");
        var missingError = Assert.Throws<PlcXmlModelException>(() => Assert.Single(missing.Networks).SetTitleText("en-US", "x"));
        Assert.Equal("PLCXML_TEXT_TARGET_NOT_FOUND", missingError.Code);
        Assert.Equal("/Document/SW.Blocks.OB[0]/ObjectList/SW.Blocks.CompileUnit[0]", missingError.Location!.Path);

        var ambiguous = Parse("<Document><SW.Blocks.OB><ObjectList><SW.Blocks.CompileUnit ID=\"N1\"><ObjectList><MultilingualText CompositionName=\"Title\"><ObjectList><MultilingualTextItem><AttributeList><Culture>en-US</Culture><Text>one</Text></AttributeList></MultilingualTextItem><MultilingualTextItem><AttributeList><Culture>en-US</Culture><Text>two</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList></SW.Blocks.CompileUnit></ObjectList></SW.Blocks.OB></Document>", "ambiguous.xml");
        var ambiguousError = Assert.Throws<PlcXmlModelException>(() => Assert.Single(ambiguous.Networks).SetTitleText("en-US", "x"));
        Assert.Equal("PLCXML_TEXT_TARGET_AMBIGUOUS", ambiguousError.Code);
        Assert.Equal("/Document/SW.Blocks.OB[0]/ObjectList/SW.Blocks.CompileUnit[0]", ambiguousError.Location!.Path);
    }

    [Fact]
    public void Serialization_without_a_pending_mutation_reports_failure_without_bytes()
    {
        var document = Parse("<Document><SW.Blocks.OB ID=\"1\" /></Document>", "serialize.xml");

        var error = Assert.Throws<PlcXmlModelException>(() => document.SerializeMutated());

        Assert.Equal("PLCXML_SERIALIZE_FAILED", error.Code);
        Assert.Equal("serialize.xml", error.SourceName);
    }

    private static PlcDocument Parse(string xml, string sourceName) =>
        PlcXmlParser.Parse(Encoding.UTF8.GetBytes(xml), sourceName);
}
