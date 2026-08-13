using System;
using System.Linq;
using Contracts;
using Contracts.Engineering;
using Contracts.Sandbox;
using Mcp.Engineering.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class OpennessFunctionContractTests
{
    [Fact]
    public void PlatformExposesProjectCapabilitiesAndExpandedConnectOptions()
    {
        var platformMethod = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.GetProjectCapabilities));
        Assert.NotNull(platformMethod);
        Assert.Equal(typeof(ProjectCapabilities), platformMethod!.ReturnType);

        var toolMethod = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.GetProjectCapabilities));
        Assert.NotNull(toolMethod);
        Assert.IsType<McpServerToolAttribute>(Assert.Single(
            toolMethod!.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)));

        var connectMethod = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.Connect));
        Assert.NotNull(connectMethod);
        Assert.Contains(connectMethod!.GetParameters(), parameter => parameter.Name == "upgrade");
        Assert.Contains(connectMethod.GetParameters(), parameter => parameter.Name == "openMode");
        Assert.Contains(connectMethod.GetParameters(), parameter => parameter.Name == "authenticationMode");
    }

    [Fact]
    public void PlatformExposesP1ProjectLifecycleFunctions()
    {
        var createMethod = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.CreateProject));
        Assert.NotNull(createMethod);
        Assert.Equal(typeof(ProjectCreateResult), createMethod!.ReturnType);
        Assert.Equal(new[] { "targetDirectory", "projectName" }, createMethod.GetParameters().Select(parameter => parameter.Name));

        var archiveMethod = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.ArchiveProject));
        Assert.NotNull(archiveMethod);
        Assert.Equal(typeof(ProjectArchiveResult), archiveMethod!.ReturnType);
        Assert.Equal(new[] { "targetDirectory", "archiveName", "archivationMode" }, archiveMethod.GetParameters().Select(parameter => parameter.Name));
        Assert.True(archiveMethod.GetParameters()[2].HasDefaultValue);

        var createTool = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.CreateProject));
        Assert.NotNull(createTool);
        Assert.IsType<McpServerToolAttribute>(Assert.Single(
            createTool!.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)));

        var archiveTool = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.ArchiveProject));
        Assert.NotNull(archiveTool);
        Assert.IsType<McpServerToolAttribute>(Assert.Single(
            archiveTool!.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)));

        var retrieveMethod = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.RetrieveProject));
        Assert.NotNull(retrieveMethod);
        Assert.Equal(typeof(ProjectRetrieveResult), retrieveMethod!.ReturnType);
        Assert.Equal(
            new[] { "archivePath", "targetDirectory", "upgrade", "openMode" },
            retrieveMethod.GetParameters().Select(parameter => parameter.Name));
        Assert.True(retrieveMethod.GetParameters()[2].HasDefaultValue);
        Assert.True(retrieveMethod.GetParameters()[3].HasDefaultValue);

        var retrieveTool = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.RetrieveProject));
        Assert.NotNull(retrieveTool);
        Assert.IsType<McpServerToolAttribute>(Assert.Single(
            retrieveTool!.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)));

        var policy = new SandboxPolicy();
        Assert.Equal(SandboxTier.Write, policy.Classify("create_project"));
        Assert.Equal(SandboxTier.Write, policy.Classify("archive_project"));
        Assert.Equal(SandboxTier.Write, policy.Classify("retrieve_project"));
    }

    [Fact]
    public void ProjectInfoContainsP0ProjectIdentityAndAccessFields()
    {
        Assert.NotNull(typeof(ProjectInfo).GetProperty(nameof(ProjectInfo.Author)));
        Assert.NotNull(typeof(ProjectInfo).GetProperty(nameof(ProjectInfo.Version)));
        Assert.NotNull(typeof(ProjectInfo).GetProperty(nameof(ProjectInfo.IsModified)));
        Assert.NotNull(typeof(ProjectInfo).GetProperty(nameof(ProjectInfo.IsReadOnly)));
        Assert.NotNull(typeof(ProjectInfo).GetProperty(nameof(ProjectInfo.IsPrimary)));
        Assert.NotNull(typeof(ProjectInfo).GetProperty(nameof(ProjectInfo.Languages)));
    }

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
