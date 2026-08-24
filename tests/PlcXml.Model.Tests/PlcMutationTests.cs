using System;
using System.IO;
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
        Assert.Equal(expected, document.SerializeMutated());
        var mutatedTree = XDocument.Load(new MemoryStream(document.SerializeMutated()), LoadOptions.PreserveWhitespace);
        var sourceTree = XDocument.Load(new MemoryStream(source), LoadOptions.PreserveWhitespace);
        Assert.Equal(sourceTree.Root!.Element("DocumentInfo")!.ToString(SaveOptions.DisableFormatting),
            mutatedTree.Root!.Element("DocumentInfo")!.ToString(SaveOptions.DisableFormatting));
    }

    [Fact]
    public void Existing_comment_mutation_matches_fixture()
    {
        var document = PlcXmlParser.Parse(FixtureFiles.ReadAllBytes(FixtureFiles.MutationSourcePath));
        Assert.Single(document.Networks).SetCommentText("en-US", "New comment");
        Assert.Equal(FixtureFiles.ReadAllBytes(FixtureFiles.MutationCommentExpectedPath), document.SerializeMutated());
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
}
