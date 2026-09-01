using System.Linq;
using Contracts;
using Contracts.Engineering;
using Contracts.Sandbox;
using Mcp.Engineering.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class PlcChecksumContractTests
{
    [Fact]
    public void PlcChecksumInfoReportsWhetherTheSoftwareIsCompiled()
    {
        var compiled = new PlcChecksumInfo { SoftwareChecksum = "checksum" };
        var unavailable = new PlcChecksumInfo { SoftwareChecksum = null };

        Assert.True(compiled.IsCompiled);
        Assert.False(unavailable.IsCompiled);
    }

    [Fact]
    public void PlcChecksumInfoCarriesAnOptionalContentFingerprint()
    {
        var property = typeof(PlcChecksumInfo).GetProperty(nameof(PlcChecksumInfo.ContentFingerprint));

        Assert.NotNull(property);
        Assert.Equal(typeof(string), property!.PropertyType);
        // Optional by contract: null when no object yielded a readable fingerprint.
        Assert.Null(new PlcChecksumInfo().ContentFingerprint);
        Assert.Equal("fingerprint", new PlcChecksumInfo { ContentFingerprint = "fingerprint" }.ContentFingerprint);
    }

    [Fact]
    public void PlatformExposesOptionalPlcNameForChecksumReads()
    {
        var method = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.GetPlcChecksums));

        Assert.NotNull(method);
        Assert.Equal(typeof(PlcChecksumInfo[]), method!.ReturnType);
        var parameters = method.GetParameters();
        Assert.Equal(new[] { "plcName" }, parameters.Select(parameter => parameter.Name));
        Assert.True(parameters[0].HasDefaultValue);
        Assert.Null(parameters[0].DefaultValue);
    }

    [Fact]
    public void EngineeringSurfaceExposesReadOnlyPlcChecksums()
    {
        var method = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.GetPlcChecksums));

        Assert.NotNull(method);
        var attribute = Assert.IsType<McpServerToolAttribute>(Assert.Single(
            method!.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)));
        Assert.Equal("get_plc_checksums", attribute.Name);
        Assert.Single(method.GetParameters());
    }

    [Fact]
    public void ChecksumReadsAreClassifiedAsReadOnlyWithoutFilesystemArguments()
    {
        var policy = new SandboxPolicy();

        Assert.Equal(SandboxTier.Read, policy.Classify("get_plc_checksums"));
        Assert.DoesNotContain(
            typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.GetPlcChecksums))!.GetParameters(),
            parameter => parameter.Name is "path" or "filePath" or "directory" or "outputDir");
    }
}
