using System.Xml.Linq;
using System.Text;
using Contracts.Sandbox;
using Mcp.SourceEditor.Models;
using Mcp.SourceEditor.Xml;
using Xunit;

namespace Mcp.SourceEditor.Tests;

public sealed class SourceEditorServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "source-editor-tests", Guid.NewGuid().ToString("N"));
    private readonly SourceEditorService service;

    public SourceEditorServiceTests()
    {
        Directory.CreateDirectory(root);
        service = new SourceEditorService(new PathJail(new[] { root }));
    }

    [Fact]
    public void Parse_ReturnsBlockAndStableNetworkTargets()
    {
        var source = CopyFixture("Main [OB1].xml");

        var result = service.Parse(source);

        Assert.Equal("Main", result.BlockName);
        Assert.Equal("OB", result.BlockType);
        Assert.NotEmpty(result.Networks);
        Assert.Equal(1, result.Networks[0].NetworkNumber);
        Assert.False(string.IsNullOrWhiteSpace(result.Networks[0].XmlId));
    }

    [Fact]
    public void Apply_UpdatesOnlyRequestedNetworkComment()
    {
        var source = CopyFixture("Main [OB1].xml");
        var parsed = service.Parse(source);
        var before = XDocument.Load(source).Descendants().First(x => x.Name.LocalName == "FlgNet").ToString();

        var result = service.Apply(source, new[]
        {
            new SourceEdit(SourceEditOperation.SetNetworkComment,
                new EditTarget(parsed.Networks[0].XmlId, 1), "zh-CN", "精确注释")
        }, null, false, false, false);

        Assert.True(File.Exists(result.OutputFilePath));
        Assert.True(result.Validation.IsValid);
        Assert.True(result.ProtectedContentMatches);
        Assert.Contains("精确注释", File.ReadAllText(result.OutputFilePath));
        var after = XDocument.Load(result.OutputFilePath).Descendants().First(x => x.Name.LocalName == "FlgNet").ToString();
        Assert.Equal(before, after);
    }

    [Fact]
    public void Apply_TargetMismatchLeavesNoOutput()
    {
        var source = CopyFixture("Main [OB1].xml");
        var parsed = service.Parse(source);
        var output = Path.Combine(root, "mismatch.xml");

        var error = Assert.Throws<SourceEditorException>(() => service.Apply(source, new[]
        {
            new SourceEdit(SourceEditOperation.SetNetworkTitle,
                new EditTarget(parsed.Networks[0].XmlId, 2), "en-US", "wrong")
        }, output, false, false, false));

        Assert.Equal("SOURCE_TARGET_MISMATCH", error.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Validate_DetectsProtectedLogicMutation()
    {
        var source = CopyFixture("Main [OB1].xml");
        var changed = Path.Combine(root, "changed.xml");
        var document = XDocument.Load(source, LoadOptions.PreserveWhitespace);
        document.Descendants().First(x => x.Name.LocalName == "FlgNet").SetAttributeValue("tampered", "true");
        document.Save(changed, SaveOptions.DisableFormatting);

        var result = service.Validate(changed, source);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, f => f.Code == "SOURCE_INTEGRITY_CHANGED");
    }

    [Fact]
    public void Apply_InPlaceRequiresDoubleConfirmation()
    {
        var source = CopyFixture("Main [OB1].xml");
        var parsed = service.Parse(source);
        var edit = new SourceEdit(SourceEditOperation.SetNetworkTitle,
            new EditTarget(parsed.Networks[0].XmlId, 1), "en-US", "Permissives");

        var error = Assert.Throws<SourceEditorException>(() =>
            service.Apply(source, new[] { edit }, null, false, true, false));

        Assert.Equal("SOURCE_IN_PLACE_CONFIRMATION_REQUIRED", error.Code);
    }

    [Fact]
    public void Validate_RejectsChangedExistingMultilingualObjectId()
    {
        var source = CopyFixture("Main [OB1].xml");
        var changed = Path.Combine(root, "changed-id.xml");
        var document = XDocument.Load(source, LoadOptions.PreserveWhitespace);
        document.Descendants().First(x => x.Name.LocalName == "MultilingualText").SetAttributeValue("ID", "FFFF");
        document.Save(changed, SaveOptions.DisableFormatting);

        var result = service.Validate(changed, source);

        Assert.False(result.IsValid);
        Assert.Contains(result.Findings, f => f.Code == "SOURCE_INTEGRITY_CHANGED");
    }

    [Fact]
    public void Parse_RejectsDtd()
    {
        var path = Path.Combine(root, "dtd.xml");
        File.WriteAllText(path, "<!DOCTYPE foo [<!ELEMENT foo ANY>]><foo />");

        var error = Assert.Throws<SourceEditorException>(() => service.Parse(path));

        Assert.Equal("SOURCE_XML_INVALID", error.Code);
    }

    [Fact]
    public void Validate_RejectsChangedMultilingualCompositionStructure()
    {
        var source = CopyFixture("Main [OB1].xml");
        var changed = Path.Combine(root, "changed-composition.xml");
        var document = XDocument.Load(source, LoadOptions.PreserveWhitespace);
        document.Descendants().First(x => x.Name.LocalName == "MultilingualText")
            .SetAttributeValue("unexpected", "unsafe");
        document.Save(changed, SaveOptions.DisableFormatting);

        Assert.False(service.Validate(changed, source).IsValid);
    }

    [Fact]
    public void SafeProperty_IsDiffedAndCannotBeWrittenTwice()
    {
        var source = CopyFixture("Main [OB1].xml");
        var result = service.Apply(source, new[]
        {
            new SourceEdit(SourceEditOperation.SetSafeProperty, null, null, "Ansel", "blockHeaderAuthor")
        }, null, false, false, false);

        var diff = service.Diff(source, result.OutputFilePath);
        Assert.Contains(diff.Changes, x => x.Field == "blockHeaderAuthor" && x.NewValue == "Ansel");

        var error = Assert.Throws<SourceEditorException>(() => service.Apply(source, new[]
        {
            new SourceEdit(SourceEditOperation.SetSafeProperty, null, null, "A", "blockHeaderAuthor"),
            new SourceEdit(SourceEditOperation.SetSafeProperty, null, null, "B", "blockHeaderAuthor"),
        }, Path.Combine(root, "duplicate.xml"), false, false, false));
        Assert.Equal("SOURCE_OPERATION_UNSUPPORTED", error.Code);
    }

    [Fact]
    public void Apply_InPlaceReportsOriginalAndOutputHashes()
    {
        var source = CopyFixture("Main [OB1].xml");
        var originalHash = service.Parse(source).Sha256;
        var parsed = service.Parse(source);

        var result = service.Apply(source, new[]
        {
            new SourceEdit(SourceEditOperation.SetNetworkTitle,
                new EditTarget(parsed.Networks[0].XmlId), "en-US", "Changed")
        }, null, false, true, true);

        Assert.Equal(originalHash, result.SourceSha256);
        Assert.NotEqual(result.SourceSha256, result.OutputSha256);
    }

    [Fact]
    public void Apply_PreservesUtf16Encoding()
    {
        var source = Path.Combine(root, "utf16.xml");
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Main [OB1].xml"));
        fixture = fixture.Replace("encoding=\"utf-8\"", "encoding=\"utf-16\"");
        File.WriteAllText(source, fixture, Encoding.Unicode);
        var parsed = service.Parse(source);

        var result = service.Apply(source, new[]
        {
            new SourceEdit(SourceEditOperation.SetNetworkComment,
                new EditTarget(parsed.Networks[0].XmlId), "en-US", "UTF16")
        }, null, false, false, false);

        var bytes = File.ReadAllBytes(result.OutputFilePath);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
    }

    [Fact]
    public void Apply_CreatesMissingCommentCompositionFromMatchingTemplate()
    {
        var source = CopyFixture("Main [OB1].xml");
        var document = XDocument.Load(source, LoadOptions.PreserveWhitespace);
        var firstUnit = document.Descendants().First(x => x.Name.LocalName == "SW.Blocks.CompileUnit");
        firstUnit.Elements().First(x => x.Name.LocalName == "ObjectList").Elements()
            .First(x => x.Name.LocalName == "MultilingualText"
                && (string?)x.Attribute("CompositionName") == "Comment").Remove();
        document.Save(source, SaveOptions.DisableFormatting);
        var parsed = service.Parse(source);

        var result = service.Apply(source, new[]
        {
            new SourceEdit(SourceEditOperation.SetNetworkComment,
                new EditTarget(parsed.Networks[0].XmlId), "en-US", "Created")
        }, null, false, false, false);

        Assert.Contains("Created", File.ReadAllText(result.OutputFilePath));
        Assert.True(result.ProtectedContentMatches);
    }

    [Fact]
    public void Validate_RejectsUnauthorizedNewEmptyComposition()
    {
        var source = CopyFixture("Main [OB1].xml");
        var changed = Path.Combine(root, "extra-composition.xml");
        var document = XDocument.Load(source, LoadOptions.PreserveWhitespace);
        var unit = document.Descendants().First(x => x.Name.LocalName == "SW.Blocks.CompileUnit");
        var objectList = unit.Elements().First(x => x.Name.LocalName == "ObjectList");
        objectList.Add(new XElement("MultilingualText",
            new XAttribute("ID", "FFFF"),
            new XAttribute("CompositionName", "Comment"),
            new XAttribute("unauthorized", "true"),
            new XElement("ObjectList")));
        document.Save(changed, SaveOptions.DisableFormatting);

        Assert.False(service.Validate(changed, source).IsValid);
    }

    [Fact]
    public void Validate_RejectsExtraNodesInsideNewComposition()
    {
        var source = CopyFixture("Main [OB1].xml");
        var document = XDocument.Load(source, LoadOptions.PreserveWhitespace);
        var firstUnit = document.Descendants().First(x => x.Name.LocalName == "SW.Blocks.CompileUnit");
        firstUnit.Elements().First(x => x.Name.LocalName == "ObjectList").Elements()
            .First(x => x.Name.LocalName == "MultilingualText"
                && (string?)x.Attribute("CompositionName") == "Comment").Remove();
        document.Save(source, SaveOptions.DisableFormatting);
        var parsed = service.Parse(source);
        var edited = service.Apply(source, new[]
        {
            new SourceEdit(SourceEditOperation.SetNetworkComment,
                new EditTarget(parsed.Networks[0].XmlId), "en-US", "Created")
        }, null, false, false, false);
        var changed = Path.Combine(root, "extra-node.xml");
        var candidate = XDocument.Load(edited.OutputFilePath, LoadOptions.PreserveWhitespace);
        var newComposition = candidate.Descendants().First(x => x.Name.LocalName == "SW.Blocks.CompileUnit")
            .Descendants().First(x => x.Name.LocalName == "MultilingualText"
                && (string?)x.Attribute("CompositionName") == "Comment");
        newComposition.Elements().First(x => x.Name.LocalName == "ObjectList")
            .Add(new XElement("UnauthorizedReference", new XAttribute("Target", "logic")));
        candidate.Save(changed, SaveOptions.DisableFormatting);

        Assert.False(service.Validate(changed, source).IsValid);
    }

    private string CopyFixture(string name)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        var destination = Path.Combine(root, name);
        File.Copy(source, destination);
        return destination;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
