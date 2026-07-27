using System.Text.Json;
using Contracts.Sandbox;
using Mcp.SourceEditor.Models;
using Mcp.SourceEditor.Tools;
using Mcp.SourceEditor.Xml;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Mcp.SourceEditor.Tests;

public sealed class SourceEditorToolsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "source-editor-tool-tests", Guid.NewGuid().ToString("N"));
    private readonly string modifiedRoot;
    private readonly SourceEditorTools tools;

    public SourceEditorToolsTests()
    {
        Directory.CreateDirectory(root);
        modifiedRoot = Path.Combine(root, "device", "modified-source");
        Directory.CreateDirectory(modifiedRoot);
        var jail = new PathJail(new[] { root });
        tools = new SourceEditorTools(new SourceEditorService(jail), jail);
    }

    [Fact]
    public void Parse_ReturnsStructuredSuccess()
    {
        var path = CopyFixture();
        var result = tools.ParseBlock(path);

        Assert.False(result.IsError);
        var payload = JsonDocument.Parse(Text(result)).RootElement;
        Assert.Equal("Main", payload.GetProperty("blockName").GetString());
    }

    [Fact]
    public void Preview_ReturnsStructuredDomainError()
    {
        var path = CopyFixture();
        var result = tools.PreviewEdits(path, new[]
        {
            new SourceEdit(SourceEditOperation.SetNetworkComment, new EditTarget("missing"), "en-US", "text")
        }, Path.Combine(modifiedRoot, "Main.xml"));

        Assert.True(result.IsError);
        var error = JsonDocument.Parse(Text(result)).RootElement.GetProperty("error");
        Assert.Equal("SOURCE_TARGET_NOT_FOUND", error.GetProperty("code").GetString());
        Assert.Equal(0, error.GetProperty("batchIndex").GetInt32());
    }

    [Fact]
    public void Apply_AllowsBaselineReadAndWritesOnlyToModifiedSource()
    {
        var baselinePath = CopyFixture();
        var parsed = tools.ParseBlock(baselinePath);
        var payload = JsonDocument.Parse(Text(parsed)).RootElement;
        var xmlId = payload.GetProperty("networks")[0].GetProperty("xmlId").GetString();
        var outputPath = Path.Combine(modifiedRoot, "Blocks", "Main.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var result = tools.ApplyEdits(
            baselinePath,
            new[]
            {
                new SourceEdit(
                    SourceEditOperation.SetNetworkComment,
                    new EditTarget(xmlId),
                    "en-US",
                    "overlay edit"),
            },
            outputPath);

        Assert.False(result.IsError);
        Assert.True(File.Exists(outputPath));
        Assert.DoesNotContain("overlay edit", File.ReadAllText(baselinePath));
        Assert.Contains("overlay edit", File.ReadAllText(outputPath));
    }

    [Fact]
    public void Apply_RejectsExportedSourceOutputWithoutChangingBaseline()
    {
        var exportedRoot = Path.Combine(root, "device", "exported-source");
        Directory.CreateDirectory(exportedRoot);
        var baselinePath = Path.Combine(exportedRoot, "Main.xml");
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Main [OB1].xml"),
            baselinePath);
        var before = File.ReadAllBytes(baselinePath);

        var result = tools.ApplyEdits(
            baselinePath,
            new[]
            {
                new SourceEdit(
                    SourceEditOperation.SetBlockTitle,
                    null,
                    "en-US",
                    "must not be written"),
            },
            baselinePath,
            overwriteOutput: true);

        Assert.True(result.IsError);
        var error = JsonDocument.Parse(Text(result)).RootElement.GetProperty("error");
        Assert.Equal("SOURCE_PATH_DENIED", error.GetProperty("code").GetString());
        Assert.Equal(before, File.ReadAllBytes(baselinePath));
    }

    [Fact]
    public void Preview_RequiresExplicitModifiedSourceOutput()
    {
        var baselinePath = CopyFixture();

        var result = tools.PreviewEdits(
            baselinePath,
            new[]
            {
                new SourceEdit(
                    SourceEditOperation.SetBlockTitle,
                    null,
                    "en-US",
                    "preview"),
            });

        Assert.True(result.IsError);
        var error = JsonDocument.Parse(Text(result)).RootElement.GetProperty("error");
        Assert.Equal("SOURCE_PATH_DENIED", error.GetProperty("code").GetString());
        Assert.False(File.Exists(Path.Combine(root, "Main.preview.xml")));
    }

    private string CopyFixture()
    {
        var destination = Path.Combine(root, "Main.xml");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Main [OB1].xml"), destination);
        return destination;
    }

    private static string Text(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text!;

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
