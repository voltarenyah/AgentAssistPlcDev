using System;
using System.Linq;
using Contracts;
using Contracts.Engineering;
using Mcp.Engineering.Tools;
using ModelContextProtocol;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class OpennessFunctionContractTests
{
    [Fact]
    public void PlatformHardwareImportExposesConflictPolicyAndLogPath()
    {
        var method = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.ImportHardwareConfiguration));

        Assert.NotNull(method);
        Assert.Equal(
            new[] { "amlFilePath", "logFilePath", "conflictPolicy" },
            method!.GetParameters().Select(parameter => parameter.Name));
        Assert.True(method.GetParameters()[1].HasDefaultValue);
        Assert.True(method.GetParameters()[2].HasDefaultValue);
    }

    [Fact]
    public void McpHardwareImportExposesConflictPolicyAndLogPath()
    {
        var method = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.ImportHardwareConfiguration));

        Assert.NotNull(method);
        Assert.Equal(
            new[] { "amlFilePath", "logFilePath", "conflictPolicy" },
            method!.GetParameters().Select(parameter => parameter.Name));
        Assert.True(method.GetParameters()[1].HasDefaultValue);
        Assert.True(method.GetParameters()[2].HasDefaultValue);
    }

    [Fact]
    public void PlatformCreateBlockExposesNativeBlockCreationArguments()
    {
        var method = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.CreateBlock));

        Assert.NotNull(method);
        Assert.Equal(
            new[] { "blockName", "blockType", "number", "programmingLanguage", "instanceOfName", "plcName" },
            method!.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void McpCreateBlockExposesNativeBlockCreationArguments()
    {
        var method = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.CreateBlock));

        Assert.NotNull(method);
        Assert.Equal(
            new[] { "blockName", "blockType", "number", "programmingLanguage", "instanceOfName", "plcName" },
            method!.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void PlatformDeleteBlockAcceptsOptionalPlcName()
    {
        var method = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.DeleteBlock));

        Assert.NotNull(method);
        Assert.Equal(new[] { "blockName", "plcName" }, method!.GetParameters().Select(parameter => parameter.Name));
        Assert.True(method.GetParameters()[1].HasDefaultValue);
        Assert.Null(method.GetParameters()[1].DefaultValue);
    }

    [Fact]
    public void McpDeleteBlockAcceptsOptionalPlcName()
    {
        var method = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.DeleteBlock));

        Assert.NotNull(method);
        Assert.Equal(new[] { "blockName", "plcName" }, method!.GetParameters().Select(parameter => parameter.Name));
        Assert.True(method.GetParameters()[1].HasDefaultValue);
        Assert.Null(method.GetParameters()[1].DefaultValue);
    }

    [Fact]
    public void PlatformHardwareExportExposesProjectAndDeviceOptions()
    {
        var method = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.ExportHardwareConfiguration));

        Assert.NotNull(method);
        Assert.Equal(
            new[] { "outputDir", "includeDeviceExports", "progress" },
            method!.GetParameters().Select(parameter => parameter.Name));
        Assert.True(method.GetParameters()[1].HasDefaultValue);
        Assert.True(method.GetParameters()[2].HasDefaultValue);
        Assert.Equal(typeof(HardwareExportResult[]), method.ReturnType);
    }

    [Fact]
    public void McpHardwareExportExposesProjectAndDeviceOptions()
    {
        var method = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.ExportHardwareConfiguration));

        Assert.NotNull(method);
        Assert.Equal(
            new[] { "outputDir", "includeDeviceExports", "progress" },
            method!.GetParameters().Select(parameter => parameter.Name));
        Assert.True(method.GetParameters()[1].HasDefaultValue);
        Assert.True(method.GetParameters()[2].HasDefaultValue);
    }
}
