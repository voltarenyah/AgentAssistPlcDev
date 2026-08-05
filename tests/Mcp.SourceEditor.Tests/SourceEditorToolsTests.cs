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
    private readonly string sourceRoot;
    private readonly SourceEditorTools tools;

    public SourceEditorToolsTests()
    {
        Directory.CreateDirectory(root);
        modifiedRoot = Path.Combine(root, "device", "modified-source");
        sourceRoot = Path.Combine(root, "devices", "device-1", "source");
        Directory.CreateDirectory(modifiedRoot);
        Directory.CreateDirectory(sourceRoot);
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
    public void Apply_ReturnsStructuredDomainError()
    {
        var path = CopyFixture();
        var result = tools.ApplyEdits(path, new[]
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
    public void Apply_AllowsJailedWorkbenchSourceInPlaceWithExplicitConfirmation()
    {
        var sourcePath = Path.Combine(sourceRoot, "Blocks", "Main.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Main [OB1].xml"),
            sourcePath);

        var result = tools.ApplyEdits(
            sourcePath,
            new[]
            {
                new SourceEdit(
                    SourceEditOperation.SetBlockTitle,
                    null,
                    "en-US",
                    "workbench source edit"),
            },
            sourcePath,
            inPlace: true,
            confirmInPlace: true,
            sourceRoot: sourceRoot);

        Assert.False(result.IsError, Text(result));
        Assert.Contains("workbench source edit", File.ReadAllText(sourcePath));
    }

    [Fact]
    public void Apply_RejectsArbitraryJailedFolderNamedSource()
    {
        var arbitraryRoot = Path.Combine(root, "arbitrary", "source");
        var sourcePath = Path.Combine(arbitraryRoot, "Main.xml");
        Directory.CreateDirectory(arbitraryRoot);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Main [OB1].xml"),
            sourcePath);
        var before = File.ReadAllBytes(sourcePath);

        var result = tools.ApplyEdits(
            sourcePath,
            new[]
            {
                new SourceEdit(
                    SourceEditOperation.SetBlockTitle,
                    null,
                    "en-US",
                    "must not be written"),
            },
            sourcePath,
            inPlace: true,
            confirmInPlace: true,
            sourceRoot: arbitraryRoot);

        Assert.True(result.IsError);
        Assert.Equal(
            "SOURCE_PATH_DENIED",
            JsonDocument.Parse(Text(result)).RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        Assert.Equal(before, File.ReadAllBytes(sourcePath));
    }

    [Fact]
    public void Apply_RejectsOutputUnderSiblingDeviceSource()
    {
        var inputPath = Path.Combine(sourceRoot, "Blocks", "Main.xml");
        var siblingRoot = Path.Combine(root, "devices", "device-2", "source");
        var siblingPath = Path.Combine(siblingRoot, "Blocks", "Main.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(inputPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(siblingPath)!);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Main [OB1].xml"),
            inputPath);

        var result = tools.ApplyEdits(
            inputPath,
            new[]
            {
                new SourceEdit(
                    SourceEditOperation.SetBlockTitle,
                    null,
                    "en-US",
                    "must not cross devices"),
            },
            siblingPath,
            inPlace: true,
            confirmInPlace: true,
            sourceRoot: sourceRoot);

        Assert.True(result.IsError);
        Assert.False(File.Exists(siblingPath));
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
    public void Apply_RequiresExplicitModifiedSourceOutput()
    {
        var baselinePath = CopyFixture();

        var result = tools.ApplyEdits(
            baselinePath,
            new[]
            {
                new SourceEdit(
                    SourceEditOperation.SetBlockTitle,
                    null,
                    "en-US",
                    "overlay"),
            });

        Assert.True(result.IsError);
        var error = JsonDocument.Parse(Text(result)).RootElement.GetProperty("error");
        Assert.Equal("SOURCE_PATH_DENIED", error.GetProperty("code").GetString());
        Assert.False(File.Exists(Path.Combine(root, "Main.edited.xml")));
    }

    [Fact]
    public void Apply_RejectsReparsePointEscapeFromWorkbenchSource()
    {
        var outside = Path.Combine(root, "outside-source");
        Directory.CreateDirectory(outside);
        var outsidePath = Path.Combine(outside, "Main.xml");
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Main [OB1].xml"),
            outsidePath);
        var before = File.ReadAllBytes(outsidePath);
        var link = Path.Combine(sourceRoot, "Blocks");

        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        try
        {
            var linkedPath = Path.Combine(link, "Main.xml");
            var result = tools.ApplyEdits(
                linkedPath,
                new[]
                {
                    new SourceEdit(
                        SourceEditOperation.SetBlockTitle,
                        null,
                        "en-US",
                        "must not escape"),
                },
                linkedPath,
                inPlace: true,
                confirmInPlace: true,
                sourceRoot: sourceRoot);

            Assert.True(result.IsError);
            var error = JsonDocument.Parse(Text(result)).RootElement.GetProperty("error");
            Assert.Equal("SOURCE_PATH_DENIED", error.GetProperty("code").GetString());
            Assert.Equal(before, File.ReadAllBytes(outsidePath));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void Apply_RejectsReparsePointDeviceSourceRoot()
    {
        var targetRoot = Path.Combine(root, "outside-device-source");
        Directory.CreateDirectory(targetRoot);
        var targetPath = Path.Combine(targetRoot, "Main.xml");
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Main [OB1].xml"),
            targetPath);
        var before = File.ReadAllBytes(targetPath);
        var linkedDeviceRoot = Path.Combine(root, "devices", "linked-device");
        var linkedSourceRoot = Path.Combine(linkedDeviceRoot, "source");
        Directory.CreateDirectory(linkedDeviceRoot);

        try
        {
            Directory.CreateSymbolicLink(linkedSourceRoot, targetRoot);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        try
        {
            var linkedPath = Path.Combine(linkedSourceRoot, "Main.xml");
            var result = tools.ApplyEdits(
                linkedPath,
                new[]
                {
                    new SourceEdit(
                        SourceEditOperation.SetBlockTitle,
                        null,
                        "en-US",
                        "must not traverse root link"),
                },
                linkedPath,
                inPlace: true,
                confirmInPlace: true,
                sourceRoot: linkedSourceRoot);

            Assert.True(result.IsError);
            Assert.Equal(before, File.ReadAllBytes(targetPath));
        }
        finally
        {
            Directory.Delete(linkedSourceRoot);
        }
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
