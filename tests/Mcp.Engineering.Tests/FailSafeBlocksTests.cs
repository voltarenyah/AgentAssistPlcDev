using System;
using Mcp.Engineering.Adapter;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class FailSafeBlocksTests
{
    [Fact]
    public void IsExportNotPermittedMatchesTheOpennessRefusal()
    {
        var refusal = new Exception(
            "Error when calling method 'Export' of type 'Siemens.Engineering.SW.Blocks.OB'. " +
            "The export of block 'FOB_SAFETY' is not permitted.");
        Assert.True(FailSafeBlocks.IsExportNotPermitted(refusal));
    }

    [Theory]
    [InlineData("Block 'FC1' is inconsistent. Compile it first before export.")]
    [InlineData("Some other Openness failure.")]
    [InlineData("")]
    public void IsExportNotPermittedRejectsOtherErrors(string message)
    {
        Assert.False(FailSafeBlocks.IsExportNotPermitted(new Exception(message)));
    }
}
