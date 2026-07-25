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
    private readonly SourceEditorTools tools;

    public SourceEditorToolsTests()
    {
        Directory.CreateDirectory(root);
        tools = new SourceEditorTools(new SourceEditorService(new PathJail(new[] { root })));
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
        });

        Assert.True(result.IsError);
        var error = JsonDocument.Parse(Text(result)).RootElement.GetProperty("error");
        Assert.Equal("SOURCE_TARGET_NOT_FOUND", error.GetProperty("code").GetString());
        Assert.Equal(0, error.GetProperty("batchIndex").GetInt32());
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
