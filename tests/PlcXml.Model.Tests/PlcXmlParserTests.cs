using System.Text;
using System.Linq;
using Xunit;

namespace PlcXml.Model.Tests;

public sealed class PlcXmlParserTests
{
    [Theory]
    [MemberData(nameof(FixtureFilesForIdentity))]
    public void Selected_fixtures_round_trip_byte_for_byte(string path)
    {
        var bytes = FixtureFiles.ReadAllBytes(path);
        Assert.Equal(bytes, PlcXmlParser.Parse(bytes, path).SerializeOriginal());
    }

    public static TheoryData<string> FixtureFilesForIdentity => new()
    {
        FixtureFiles.MainObPath, FixtureFiles.SimulateCylinderFcPath, FixtureFiles.SclAssignFcPath
    };

    [Fact]
    public void SerializeOriginal_returns_exact_input_bytes_and_does_not_expose_mutable_storage()
    {
        var input = Encoding.UTF8.GetBytes("\uFEFF<?xml version=\"1.0\"?><Document>\r\n  <SW.Blocks.OB ID=\"1\" />\r\n</Document>");
        var document = PlcXmlParser.Parse(input, "identity.xml");

        var serialized = document.SerializeOriginal();
        Assert.Equal(input, serialized);
        serialized[0] = 0;
        Assert.Equal(input, document.SerializeOriginal());
    }

    [Fact]
    public void Parse_builds_ordered_generic_spine_and_cloned_raw_content()
    {
        var input = Encoding.UTF8.GetBytes("<Document><SW.Blocks.OB ID=\"B1\" CompositionName=\"Blocks\"><AttributeList><Name>Main</Name><Unknown><Nested /></Unknown></AttributeList><ObjectList><SW.Blocks.CompileUnit ID=\"C1\"><AttributeList><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList><UnknownChild /></SW.Blocks.CompileUnit><SW.Blocks.CompileUnit ID=\"C2\" /></ObjectList><Trailing /></SW.Blocks.OB></Document>");

        var document = PlcXmlParser.Parse(input);
        var root = Assert.Single(document.Objects);
        Assert.Equal("B1", root.Id);
        Assert.Equal("SW.Blocks.OB", root.ElementName);
        Assert.Equal("Main", Assert.Single(root.Attributes, a => a.Name == "Name").Value);
        Assert.Equal(new[] { "C1", "C2" }, root.Compositions.Select(o => o.Id));
        var raw = Assert.Single(root.RawValues, value => value.Name == "Trailing");
        var clone = raw.Element;
        clone!.SetAttributeValue("changed", "yes");
        Assert.DoesNotContain("changed", root.RawValues.Single(v => v.Name == "Trailing").Element!.ToString());
        Assert.Equal("C1", root.Compositions[0].Location.Id);
    }

    [Theory]
    [InlineData("<NotAPlcDocument />", "PLCXML_ROOT_UNSUPPORTED")]
    [InlineData("<Document><Broken></Document>", "PLCXML_PARSE_INVALID")]
    [InlineData("<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///secret'>]><Document>&x;</Document>", "PLCXML_PARSE_INVALID")]
    public void Invalid_or_unsafe_xml_throws_model_specific_exception(string xml, string code)
    {
        var exception = Assert.Throws<PlcXmlParseException>(() => PlcXmlParser.Parse(Encoding.UTF8.GetBytes(xml), "invalid.xml"));
        Assert.Equal(code, exception.Code);
        Assert.Equal("invalid.xml", exception.SourceName);
    }
}
