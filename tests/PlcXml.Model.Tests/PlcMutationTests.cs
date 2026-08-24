using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Xunit;

namespace PlcXml.Model.Tests;

public sealed class PlcMutationTests
{
    [Fact]
    public void Existing_title_mutation_matches_fixture_and_preserves_original_and_non_target_tree()
    {
        var source = FixtureFiles.ReadAllBytes(FixtureFiles.MutationSourcePath);
        var document = PlcXmlParser.Parse(source, FixtureFiles.MutationSourcePath);
        var network = Assert.Single(document.Networks);

        network.SetTitleText("en-US", "New title");

        Assert.Equal(source, document.SerializeOriginal());
        var expected = FixtureFiles.ReadAllBytes(FixtureFiles.MutationTitleExpectedPath);
        var mutated = document.SerializeMutated();
        Assert.Equal(expected, mutated);
        var mutatedTree = XDocument.Load(new MemoryStream(mutated), LoadOptions.PreserveWhitespace);
        var sourceTree = XDocument.Load(new MemoryStream(source), LoadOptions.PreserveWhitespace);
        AssertOnlyTextDelta(sourceTree, mutatedTree, "Title", "en-US", "New title");
        Assert.Equal(sourceTree.Root!.Element("DocumentInfo")!.ToString(SaveOptions.DisableFormatting),
            mutatedTree.Root!.Element("DocumentInfo")!.ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void Existing_comment_mutation_matches_fixture()
    {
        var document = PlcXmlParser.Parse(FixtureFiles.ReadAllBytes(FixtureFiles.MutationSourcePath));
        Assert.Single(document.Networks).SetCommentText("en-US", "New comment");
        var mutated = document.SerializeMutated();
        Assert.Equal(FixtureFiles.ReadAllBytes(FixtureFiles.MutationCommentExpectedPath), mutated);
        var sourceTree = XDocument.Load(FixtureFiles.MutationSourcePath, LoadOptions.PreserveWhitespace);
        var mutatedTree = XDocument.Load(new MemoryStream(mutated), LoadOptions.PreserveWhitespace);
        AssertOnlyTextDelta(sourceTree, mutatedTree, "Comment", "en-US", "New comment");
    }

    [Fact]
    public void Duplicate_target_is_rejected()
    {
        var document = PlcXmlParser.Parse(FixtureFiles.ReadAllBytes(FixtureFiles.MutationSourcePath));
        var network = Assert.Single(document.Networks);
        network.SetTitleText("en-US", "one");
        var error = Assert.Throws<PlcXmlModelException>(() => network.SetTitleText("en-US", "two"));
        Assert.Equal("PLCXML_TEXT_TARGET_AMBIGUOUS", error.Code);
    }

    [Fact]
    public void Existing_fixture_mutation_preserves_bom_and_crlf()
    {
        var source = FixtureFiles.ReadAllBytes(FixtureFiles.MainObPath);
        var document = PlcXmlParser.Parse(source, FixtureFiles.MainObPath);
        Assert.True(document.HasBom);
        Assert.True(document.UsesCrLf);

        document.Networks[0].SetTitleText("en-US", "Changed title");

        var mutated = document.SerializeMutated();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, mutated[..3]);
        Assert.True(mutated.AsSpan().IndexOf(new byte[] { 0x0D, 0x0A }) >= 0);
        Assert.Equal(source, document.SerializeOriginal());
    }

    [Fact]
    public void Duplicate_network_ids_resolve_by_selected_model_occurrence()
    {
        var xml = "<Document><SW.Blocks.OB ID=\"B\"><ObjectList>" +
            NetworkXml("same", "first") + NetworkXml("same", "second") +
            "</ObjectList></SW.Blocks.OB></Document>";
        var document = PlcXmlParser.Parse(Encoding.UTF8.GetBytes(xml), "duplicate.xml");

        Assert.Equal(2, document.Networks.Count);
        document.Networks[1].SetTitleText("en-US", "changed");

        var resultXml = Encoding.UTF8.GetString(document.SerializeMutated());
        Assert.Contains("<Text>first</Text>", resultXml);
        Assert.Contains("<Text>changed</Text>", resultXml);
    }

    [Fact]
    public void Missing_target_diagnostic_includes_source_and_model_location()
    {
        var document = PlcXmlParser.Parse(Encoding.UTF8.GetBytes("<Document><SW.Blocks.OB ID=\"B\"><ObjectList>" +
            NetworkXml("N1", "first") + "</ObjectList></SW.Blocks.OB></Document>"), "missing.xml");
        var error = Assert.Throws<PlcXmlModelException>(() => document.Networks[0].SetCommentText("de-DE", "x"));
        Assert.Equal("missing.xml", error.SourceName);
        Assert.Contains(document.Networks[0].Location.Path, error.Message);
    }

    private static string NetworkXml(string id, string title) =>
        $"<SW.Blocks.CompileUnit ID=\"{id}\" CompositionName=\"CompileUnits\"><ObjectList>" +
        $"<MultilingualText ID=\"{id}T\" CompositionName=\"Title\"><ObjectList><MultilingualTextItem ID=\"{id}I\"><AttributeList><Culture>en-US</Culture><Text>{title}</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText>" +
        "</ObjectList></SW.Blocks.CompileUnit>";

    private static void AssertOnlyTextDelta(XDocument source, XDocument result, string field, string culture, string replacement)
    {
        Assert.NotNull(source.Root);
        Assert.NotNull(result.Root);
        Assert.Single(source.Descendants(), e => IsTargetText(e, field, culture));
        var resultTarget = Assert.Single(result.Descendants(), e => IsTargetText(e, field, culture));
        Assert.Equal(replacement, resultTarget.Value);
        CompareElements(source.Root!, result.Root!, field, culture, replacement);
    }

    private static bool IsTargetText(XElement element, string field, string culture)
    {
        var item = element.Parent?.Parent;
        var composition = item?.Parent?.Parent;
        return element.Name.LocalName == "Text" && item?.Name.LocalName == "MultilingualTextItem" &&
            composition?.Name.LocalName == "MultilingualText" &&
            (string?)composition.Attribute("CompositionName") == field &&
            element.Parent?.Elements().FirstOrDefault(e => e.Name.LocalName == "Culture")?.Value == culture;
    }

    private static void CompareElements(XElement source, XElement result, string field, string culture, string replacement)
    {
        Assert.Equal(source.Name, result.Name);
        Assert.Equal(source.Attributes().Select(a => (a.Name, a.Value)), result.Attributes().Select(a => (a.Name, a.Value)));
        var isTarget = IsTargetText(source, field, culture);
        if (isTarget)
        {
            Assert.Equal(replacement, result.Value);
            return;
        }

        var sourceNodes = source.Nodes().ToList();
        var resultNodes = result.Nodes().ToList();
        Assert.Equal(sourceNodes.Count, resultNodes.Count);
        for (var i = 0; i < sourceNodes.Count; i++)
        {
            switch (sourceNodes[i], resultNodes[i])
            {
                case (XElement left, XElement right):
                    CompareElements(left, right, field, culture, replacement);
                    break;
                case (XText left, XText right):
                    Assert.Equal(left.Value, right.Value);
                    break;
                case (XComment left, XComment right):
                    Assert.Equal(left.Value, right.Value);
                    break;
                case (XProcessingInstruction left, XProcessingInstruction right):
                    Assert.Equal(left.Target, right.Target);
                    Assert.Equal(left.Data, right.Data);
                    break;
                default:
                    Assert.Fail($"Node mismatch at child index {i}: {sourceNodes[i].NodeType} vs {resultNodes[i].NodeType}.");
                    break;
            }
        }
    }
}
