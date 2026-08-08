using System;
using System.IO;
using System.Linq;
using Contracts;
using Contracts.Sandbox;
using Mcp.Engineering.Sandbox;
using Mcp.Engineering.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class SaveProjectAsContractTests : IDisposable
{
    private readonly string sandboxRoot;

    public SaveProjectAsContractTests()
    {
        sandboxRoot = Path.Combine(Path.GetTempPath(), "save-project-as-jail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandboxRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(sandboxRoot))
        {
            Directory.Delete(sandboxRoot, recursive: true);
        }
    }

    [Fact]
    public void PlatformSaveProjectAsTakesADirectoryAndReturnsTheManagedPath()
    {
        var method = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.SaveProjectAs));

        Assert.NotNull(method);
        Assert.Equal(typeof(string), method!.ReturnType);
        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal("targetDirectory", parameter.Name);
        Assert.Equal(typeof(DirectoryInfo), parameter.ParameterType);
    }

    [Fact]
    public void McpSaveProjectAsIsRegisteredWithTheJailedTargetDirectoryArgument()
    {
        var method = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.SaveProjectAs));

        Assert.NotNull(method);
        var attribute = Assert.IsType<McpServerToolAttribute>(Assert.Single(
            method!.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)));
        Assert.Equal("save_project_as", attribute.Name);
        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal("targetDirectory", parameter.Name);
        Assert.Equal(typeof(string), parameter.ParameterType);
    }

    [Fact]
    public void SaveProjectAsPersistsUserWorkAndIsClassifiedDestructive()
    {
        Assert.Equal(SandboxTier.Destructive, new SandboxPolicy().Classify("save_project_as"));
    }

    [Fact]
    public void GuardRejectsATargetDirectoryOutsideTheJail()
    {
        var allowed = Directory.CreateDirectory(Path.Combine(sandboxRoot, "allowed"));
        var outside = Path.Combine(sandboxRoot, "outside");
        var guard = CreateGuard(allowed.FullName);

        var ex = Assert.Throws<SandboxException>(
            () => guard.Check("save_project_as", ("targetDirectory", outside)));
        Assert.Equal("SANDBOX_PATH_DENIED", ex.Code);
    }

    [Fact]
    public void GuardAdmitsATargetDirectoryInsideTheJail()
    {
        var allowed = Directory.CreateDirectory(Path.Combine(sandboxRoot, "allowed"));
        var guard = CreateGuard(allowed.FullName);

        var tier = guard.Check("save_project_as", ("targetDirectory", Path.Combine(allowed.FullName, "managed")));

        Assert.Equal(SandboxTier.Destructive, tier);
    }

    private EngineeringGuard CreateGuard(string allowedRoot)
    {
        var configPath = Path.Combine(sandboxRoot, "sandbox.json");
        File.WriteAllText(configPath,
            "{\"allowedRoots\": [\"" + allowedRoot.Replace("\\", "\\\\") + "\"], " +
            "\"auditDirectory\": \"" + Path.Combine(sandboxRoot, "audit").Replace("\\", "\\\\") + "\"}");
        var config = SandboxConfig.Load(configPath);
        return new EngineeringGuard(config, new SandboxAudit(config.AuditDirectory, "save-project-as-test"));
    }
}
