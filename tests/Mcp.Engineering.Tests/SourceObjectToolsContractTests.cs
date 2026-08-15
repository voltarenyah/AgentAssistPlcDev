using System.Linq;
using System.Reflection;
using Contracts;
using Contracts.Engineering;
using Contracts.Sandbox;
using Mcp.Engineering.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class SourceObjectToolsContractTests
{
    [Theory]
    [InlineData("OB", "OB")]
    [InlineData("fb", "FB")]
    [InlineData(" FC ", "FC")]
    [InlineData("DB", "DB")]
    [InlineData("Tags", "Tags")]
    [InlineData("tags", "Tags")]
    [InlineData("UDT", "UDT")]
    [InlineData("udt", "UDT")]
    public void CategoryNormalizesToManifestValues(string input, string expected)
    {
        Assert.Equal(expected, SourceObjectCategory.Normalize(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Hardware")]
    public void UnknownCategoryNormalizesToNull(string? input)
    {
        Assert.Null(SourceObjectCategory.Normalize(input));
    }

    [Fact]
    public void EngineeringSurfaceExposesSourceObjectExport()
    {
        var method = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.ExportSourceObject));

        Assert.NotNull(method);
        Assert.Equal(
            "export_source_object",
            Assert.Single(method!.GetCustomAttributes<McpServerToolAttribute>()).Name);
        Assert.Equal(
            new[] { "name", "category", "outputDir", "plcName" },
            method.GetParameters().Select(parameter => parameter.Name));
        Assert.True(method.GetParameters()[3].HasDefaultValue);
    }

    [Fact]
    public void EngineeringSurfaceExposesSourceObjectOpenInEditor()
    {
        var method = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.OpenSourceObjectInEditor));

        Assert.NotNull(method);
        Assert.Equal(
            "open_source_object_in_editor",
            Assert.Single(method!.GetCustomAttributes<McpServerToolAttribute>()).Name);
        Assert.Equal(
            new[] { "name", "category", "plcName" },
            method.GetParameters().Select(parameter => parameter.Name));
        Assert.True(method.GetParameters()[2].HasDefaultValue);
    }

    [Fact]
    public void PlatformSurfaceExposesBothSourceObjectOperations()
    {
        var export = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.ExportSourceObject));
        Assert.NotNull(export);
        Assert.Equal(typeof(ExportResult), export!.ReturnType);
        Assert.Equal(
            new[] { "name", "category", "outputDir", "plcName" },
            export.GetParameters().Select(parameter => parameter.Name));

        var open = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.OpenSourceObjectInEditor));
        Assert.NotNull(open);
        Assert.Equal(typeof(OpenInEditorResult), open!.ReturnType);
        Assert.Equal(
            new[] { "name", "category", "plcName" },
            open.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void SourceObjectToolsClassifyLikeTheirBlockCounterparts()
    {
        var policy = new SandboxPolicy();
        Assert.Equal(SandboxTier.Read, policy.Classify("export_source_object"));
        Assert.Equal(SandboxTier.Write, policy.Classify("open_source_object_in_editor"));
    }
}
