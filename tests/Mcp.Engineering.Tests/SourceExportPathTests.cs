using Mcp.Engineering.Export;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class SourceExportPathTests
{
    [Theory]
    [InlineData("Blocks", "Area/Conveyors", "Main [OB1].xml", "Blocks/Area/Conveyors/Main [OB1].xml")]
    [InlineData("Tags", "LineA", "Inputs.xml", "Tags/LineA/Inputs.xml")]
    [InlineData("UDT", null, "Motor.xml", "UDT/Motor.xml")]
    public void ExportPathPreservesPlcGroupHierarchy(
        string category, string? groupPath, string fileName, string expected)
    {
        Assert.Equal(expected, SourceExportPath.Build(category, groupPath, fileName));
    }
}
