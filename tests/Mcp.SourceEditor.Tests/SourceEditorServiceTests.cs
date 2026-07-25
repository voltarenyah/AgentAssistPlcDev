using System.Xml.Linq;
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
    public void Preview_UpdatesOnlyRequestedNetworkComment()
    {
        var source = CopyFixture("Main [OB1].xml");
        var parsed = service.Parse(source);
        var before = XDocument.Load(source).Descendants().First(x => x.Name.LocalName == "FlgNet").ToString();

        var result = service.Preview(source, new[]
        {
            new SourceEdit(SourceEditOperation.SetNetworkComment,
                new EditTarget(parsed.Networks[0].XmlId, 1), "zh-CN", "精确注释")
        }, null, false);

        Assert.True(File.Exists(result.OutputFilePath));
        Assert.True(result.Validation.IsValid);
        Assert.True(result.ProtectedContentMatches);
        Assert.Contains("精确注释", File.ReadAllText(result.OutputFilePath));
        var after = XDocument.Load(result.OutputFilePath).Descendants().First(x => x.Name.LocalName == "FlgNet").ToString();
        Assert.Equal(before, after);
    }

    [Fact]
    public void Preview_TargetMismatchLeavesNoOutput()
    {
        var source = CopyFixture("Main [OB1].xml");
        var parsed = service.Parse(source);
        var output = Path.Combine(root, "mismatch.xml");

        var error = Assert.Throws<SourceEditorException>(() => service.Preview(source, new[]
        {
            new SourceEdit(SourceEditOperation.SetNetworkTitle,
                new EditTarget(parsed.Networks[0].XmlId, 2), "en-US", "wrong")
        }, output, false));

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
