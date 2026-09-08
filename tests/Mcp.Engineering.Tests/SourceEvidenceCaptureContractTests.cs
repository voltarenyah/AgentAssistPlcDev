using System.Linq;
using Contracts;
using Contracts.Engineering;
using Mcp.Engineering.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class SourceEvidenceCaptureContractTests
{
    [Fact]
    public void PlatformExposesInitialAndBaselineEvidenceCaptureOperations()
    {
        var capture = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.CaptureSourceEvidence));
        var compare = typeof(IEngineeringPlatform).GetMethod(nameof(IEngineeringPlatform.CompareSourceEvidence));

        Assert.NotNull(capture);
        Assert.Equal(typeof(SourceEvidenceCaptureResult), capture!.ReturnType);
        Assert.Equal(new[] { "plcName" }, capture.GetParameters().Select(parameter => parameter.Name));

        Assert.NotNull(compare);
        Assert.Equal(typeof(SourceEvidenceCaptureResult), compare!.ReturnType);
        Assert.Equal(new[] { "baseline", "outputDir", "plcName" }, compare.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public void ToolSurfaceExposesReadOnlyInitialAndCandidateOnlyCompareOperations()
    {
        var capture = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.CaptureSourceEvidence));
        var compare = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.CompareSourceEvidence));

        Assert.NotNull(capture);
        Assert.NotNull(compare);
        Assert.IsType<McpServerToolAttribute>(Assert.Single(capture!.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)));
        Assert.IsType<McpServerToolAttribute>(Assert.Single(compare!.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false)));
    }
}
