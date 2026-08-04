using System;
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

public sealed class SourceObjectImportContractTests
{
    [Theory]
    [InlineData("Blocks/Area/Main [OB1].xml", "Block")]
    [InlineData("DB/Recipes/Recipe [DB10].xml", "Block")]
    [InlineData("Tags/LineA/Inputs.xml", "TagTable")]
    [InlineData("UDT/Models/Motor.xml", "Udt")]
    public void RelativePathClassifiesSupportedExistingObject(string path, string expected)
    {
        Assert.Equal(expected, SourceObjectImport.Classify(path).ToString());
    }

    [Fact]
    public void EngineeringSurfaceExposesGenericSourceImport()
    {
        var method = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.ImportSourceObject));

        Assert.NotNull(method);
        Assert.Equal(
            "import_source_object",
            Assert.Single(method!.GetCustomAttributes<McpServerToolAttribute>()).Name);
        Assert.Equal(
            new[] { "relativePath", "xmlFilePath", "plcName" },
            method.GetParameters().Select(parameter => parameter.Name));
        Assert.True(method.GetParameters()[2].HasDefaultValue);
        Assert.Null(method.GetParameters()[2].DefaultValue);
    }

    [Fact]
    public void PlatformSurfaceExposesGenericSourceImport()
    {
        var method = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.ImportSourceObject));

        Assert.NotNull(method);
        Assert.Equal(typeof(SourceObjectImportResult), method!.ReturnType);
        Assert.Equal(
            new[] { "relativePath", "xmlFilePath", "plcName" },
            method.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void GenericSourceImportIsDestructive()
    {
        Assert.Equal(SandboxTier.Destructive, new SandboxPolicy().Classify("import_source_object"));
    }
}
