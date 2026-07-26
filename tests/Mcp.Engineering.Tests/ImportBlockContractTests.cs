using System.Linq;
using Contracts;
using Mcp.Engineering.Tools;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class ImportBlockContractTests
{
    [Fact]
    public void PlatformImportBlockAcceptsOptionalPlcName()
    {
        var method = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.ImportBlock));

        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Equal(new[] { "blockName", "xmlFilePath", "plcName" }, parameters.Select(parameter => parameter.Name));
        Assert.True(parameters[2].HasDefaultValue);
        Assert.Null(parameters[2].DefaultValue);
    }

    [Fact]
    public void McpImportBlockExposesOptionalPlcName()
    {
        var method = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.ImportBlock));

        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Equal(new[] { "blockName", "xmlFilePath", "plcName" }, parameters.Select(parameter => parameter.Name));
        Assert.True(parameters[2].HasDefaultValue);
        Assert.Null(parameters[2].DefaultValue);
    }

    [Fact]
    public void PlatformCompileBlockAcceptsOptionalPlcName()
    {
        var method = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.CompileBlock));

        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Equal(new[] { "blockName", "plcName" }, parameters.Select(parameter => parameter.Name));
        Assert.True(parameters[1].HasDefaultValue);
        Assert.Null(parameters[1].DefaultValue);
    }

    [Fact]
    public void McpCompileBlockExposesOptionalPlcName()
    {
        var method = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.CompileBlock));

        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Equal(new[] { "blockName", "plcName" }, parameters.Select(parameter => parameter.Name));
        Assert.True(parameters[1].HasDefaultValue);
        Assert.Null(parameters[1].DefaultValue);
    }
}
