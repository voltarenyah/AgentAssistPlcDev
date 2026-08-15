using System;
using Mcp.Engineering.Adapter;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class FailSafeBlocksTests
{
    [Theory]
    [InlineData("F_LAD", true)]
    [InlineData("F_FBD", true)]
    [InlineData("F_STL", true)]
    [InlineData("F_DB", true)]
    [InlineData("LAD", false)]
    [InlineData("FBD", false)]
    [InlineData("STL", false)]
    [InlineData("SCL", false)]
    [InlineData("DB", false)]
    [InlineData("GRAPH", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsFailSafeLanguageMatchesOnlyFailSafeDialects(string? language, bool expected)
    {
        Assert.Equal(expected, FailSafeBlocks.IsFailSafeLanguage(language));
    }

    [Theory]
    [InlineData("FOB_SAFETY", true)]
    [InlineData("FFB_SAFETY", true)]
    [InlineData("FFC_SAFETY", true)]
    [InlineData("FDB_SAFETY", true)]
    [InlineData("FB_SAFETY", false)]
    [InlineData("Main_OB1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsFailSafeNameMatchesGeneratedFSafetyBlockPrefixes(string? name, bool expected)
    {
        Assert.Equal(expected, FailSafeBlocks.IsFailSafeName(name));
    }

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
