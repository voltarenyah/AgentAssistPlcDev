using Mcp.Engineering.Export;
using Xunit;

namespace Mcp.Engineering.Tests;

/// <summary>
/// The rule a path-scoped source read rests on: only an object whose manifest identity re-derives the
/// requested id may be read in place of the complete walk; everything else must fall back to it.
/// </summary>
public sealed class ScopedSourceRequestTests
{
    [Theory]
    [InlineData("Main", null, "Main")]
    [InlineData("15_VOC_Door/202_VocDoorGeneral", "15_VOC_Door", "202_VocDoorGeneral")]
    [InlineData("A/B/C", "A/B", "C")]
    public void SplitsAManifestSourcePathIntoItsGroupPathAndName(string sourcePath, string? groupPath, string name)
    {
        Assert.True(ScopedSourceRequest.TrySplit(sourcePath, out var foundGroupPath, out var foundName));

        Assert.Equal(groupPath, foundGroupPath);
        Assert.Equal(name, foundName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/Main")]
    [InlineData("Group/")]
    public void RefusesAPathThatNamesNothing(string? sourcePath)
    {
        Assert.False(ScopedSourceRequest.TrySplit(sourcePath, out var groupPath, out var name));

        Assert.Null(groupPath);
        Assert.Empty(name);
    }

    [Fact]
    public void AcceptsTheObjectTheRequestAskedFor()
    {
        var id = StableId.Create("FB", "15_VOC_Door/202_VocDoorGeneral");

        Assert.True(ScopedSourceRequest.MatchesRequestedId(id, "FB", "15_VOC_Door/202_VocDoorGeneral"));
    }

    [Theory]
    // A different category is a different object (a fail-safe block is keyed as "F", not as its
    // programming language), so an id that disagrees must send the caller back to the complete walk.
    [InlineData("OB")]
    [InlineData("F")]
    [InlineData("UDT")]
    public void RejectsAnObjectWhoseIdentityDiffersFromTheRequest(string category)
    {
        var id = StableId.Create("FB", "15_VOC_Door/202_VocDoorGeneral");

        Assert.False(ScopedSourceRequest.MatchesRequestedId(id, category, "15_VOC_Door/202_VocDoorGeneral"));
    }

    [Fact]
    public void RejectsAnObjectAtAnotherPath()
    {
        var id = StableId.Create("FB", "15_VOC_Door/202_VocDoorGeneral");

        Assert.False(ScopedSourceRequest.MatchesRequestedId(id, "FB", "15_VOC_Door/203_Other"));
        Assert.False(ScopedSourceRequest.MatchesRequestedId(id, "FB", "202_VocDoorGeneral"));
    }
}
