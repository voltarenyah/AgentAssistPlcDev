using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mcp.Knowledge.Import;
using Xunit;

namespace Mcp.Knowledge.Tests;

public sealed class EffectiveSourceImporterTests
{
    [Fact]
    public void FullImportUsesOverlayForManifestPathAndBaselineForOtherComponents()
    {
        using var exported = new TempExportTree();
        using var modified = new TempExportTree();
        const string aPath = "Blocks/A.xml";
        const string bPath = "Blocks/B.xml";
        exported.AddText(aPath, Ob("A", "baseline-a"));
        exported.AddText(bPath, Ob("B", "baseline-b"));
        ManifestFixtures.Write(
            exported,
            ManifestFixtures.Component("A", "OB", aPath, "Program blocks/A"),
            ManifestFixtures.Component("B", "OB", bPath, "Program blocks/B"));
        var overlayPath = modified.AddText(aPath, Ob("A", "modified-a"));

        var result = EffectiveSourceImporter.Import(exported.Root, modified.Root);

        Assert.Equal(2, result.FilesFound);
        Assert.Equal(2, result.FilesImported);
        Assert.Equal("effective-manifest", result.Source);
        Assert.Equal(
            Sha256(overlayPath),
            result.Components.Single(component => component.RelativePath == aPath).ContentHash);
        Assert.Equal(
            Sha256(Path.Combine(exported.Root, bPath)),
            result.Components.Single(component => component.RelativePath == bPath).ContentHash);
        Assert.Contains(
            result.Graph.Nodes,
            node => node.Id.StartsWith("network:A:", StringComparison.Ordinal));
        Assert.Equal(
            "baseline-b",
            result.Graph.Nodes
                .Single(node => node.Id.StartsWith("network:B:", StringComparison.Ordinal))
                .Properties["title"]);
    }

    [Fact]
    public void OverlayAtManifestPathMustKeepManifestIdentity()
    {
        using var exported = new TempExportTree();
        using var modified = new TempExportTree();
        const string relativePath = "Blocks/A.xml";
        exported.AddText(relativePath, Ob("A", "baseline"));
        ManifestFixtures.Write(
            exported,
            ManifestFixtures.Component("A", "OB", relativePath, "Program blocks/A"));
        modified.AddText(relativePath, Ob("Different", "modified"));

        var error = Assert.Throws<ComponentIdentityMismatchException>(() =>
            EffectiveSourceImporter.Import(exported.Root, modified.Root));

        Assert.Equal("COMPONENT_IDENTITY_MISMATCH", error.Code);
        Assert.Contains(relativePath, error.Message);
    }

    [Fact]
    public void FullImportClassifiesValidOverlayOnlyComponentWithPathIdentity()
    {
        using var exported = new TempExportTree();
        using var modified = new TempExportTree();
        const string baselinePath = "Blocks/A.xml";
        const string addedPath = "Blocks/New.xml";
        exported.AddText(baselinePath, Ob("A", "baseline"));
        ManifestFixtures.Write(
            exported,
            ManifestFixtures.Component("A", "OB", baselinePath, "Program blocks/A"));
        modified.AddText(addedPath, Ob("New", "added"));

        var result = EffectiveSourceImporter.Import(exported.Root, modified.Root);

        var added = Assert.Single(
            result.Components,
            component => component.RelativePath == addedPath);
        Assert.Equal($"path:{addedPath}", added.ComponentKey);
        Assert.Contains(result.Graph.Nodes, node => node.Id == "block:New");
    }

    [Fact]
    public void OverlayOnlyComponentWithoutXmlNameIsRejected()
    {
        using var exported = new TempExportTree();
        using var modified = new TempExportTree();
        const string baselinePath = "Blocks/A.xml";
        exported.AddText(baselinePath, Ob("A", "baseline"));
        ManifestFixtures.Write(
            exported,
            ManifestFixtures.Component("A", "OB", baselinePath, "Program blocks/A"));
        modified.AddText(
            "Blocks/New.xml",
            "<Document><SW.Blocks.OB><AttributeList /></SW.Blocks.OB></Document>");

        var error = Assert.Throws<ManifestInvalidException>(() =>
            EffectiveSourceImporter.Import(exported.Root, modified.Root));

        Assert.Contains("has no component identity", error.Message);
    }

    private static string Ob(string name, string networkTitle)
    {
        return $$"""
            <Document>
              <SW.Blocks.OB ID="0">
                <AttributeList>
                  <Name>{{name}}</Name>
                  <ProgrammingLanguage>LAD</ProgrammingLanguage>
                </AttributeList>
                <ObjectList>
                  <SW.Blocks.CompileUnit ID="1" CompositionName="CompileUnits">
                    <AttributeList>
                      <NetworkSource><FlgNet /></NetworkSource>
                      <ProgrammingLanguage>LAD</ProgrammingLanguage>
                    </AttributeList>
                    <ObjectList>
                      <MultilingualText ID="2" CompositionName="Title">
                        <ObjectList>
                          <MultilingualTextItem ID="3" CompositionName="Items">
                            <AttributeList><Text>{{networkTitle}}</Text></AttributeList>
                          </MultilingualTextItem>
                        </ObjectList>
                      </MultilingualText>
                    </ObjectList>
                  </SW.Blocks.CompileUnit>
                </ObjectList>
              </SW.Blocks.OB>
            </Document>
            """;
    }

    private static string Sha256(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();
    }
}
