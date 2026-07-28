using System;
using System.Linq;
using Contracts;
using Contracts.Engineering;
using Mcp.Engineering.Tools;
using ModelContextProtocol;
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

    [Theory]
    [InlineData(nameof(IEngineeringPlatform.ExportAllBlocks))]
    [InlineData(nameof(IEngineeringPlatform.ExportTagTables))]
    [InlineData(nameof(IEngineeringPlatform.ExportUdts))]
    [InlineData(nameof(IEngineeringPlatform.SyncExport))]
    [InlineData(nameof(IEngineeringPlatform.RebuildExport))]
    public void PlatformExportOperationsAcceptOptionalProgress(string methodName)
    {
        var method = typeof(IEngineeringPlatform).GetMethod(methodName);

        Assert.NotNull(method);
        var progress = method!.GetParameters().SingleOrDefault(parameter => parameter.Name == "progress");
        Assert.NotNull(progress);
        Assert.Equal(typeof(IProgress<EngineeringProgress>), progress!.ParameterType);
        Assert.True(progress.HasDefaultValue);
        Assert.Null(progress.DefaultValue);
    }

    [Theory]
    [InlineData(nameof(EngineeringTools.ExportAllBlocks))]
    [InlineData(nameof(EngineeringTools.ExportTagTables))]
    [InlineData(nameof(EngineeringTools.ExportUdts))]
    [InlineData(nameof(EngineeringTools.SyncExport))]
    [InlineData(nameof(EngineeringTools.RebuildExport))]
    public void McpExportOperationsExposeOptionalProgress(string methodName)
    {
        var method = typeof(EngineeringTools).GetMethod(methodName);

        Assert.NotNull(method);
        var progress = method!.GetParameters().SingleOrDefault(parameter => parameter.Name == "progress");
        Assert.NotNull(progress);
        Assert.Equal(typeof(IProgress<ProgressNotificationValue>), progress!.ParameterType);
        Assert.True(progress.HasDefaultValue);
        Assert.Null(progress.DefaultValue);
    }
}
