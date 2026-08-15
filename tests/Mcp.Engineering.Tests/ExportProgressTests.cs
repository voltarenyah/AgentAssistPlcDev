using Mcp.Engineering.Export;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class ExportProgressTests
{
    [Fact]
    public void ExportedSourceFileMessage_UsesAnIncrementingCount()
    {
        var counter = new ExportProgressCounter();

        Assert.Equal("Exported PLC source files: 1", counter.NextMessage());
        Assert.Equal("Exported PLC source files: 2", counter.NextMessage());
        Assert.Equal(2, counter.Count);
    }
}
