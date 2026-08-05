using Agent.Mcp;
using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class McpServerConnectionTests
{
    [Fact]
    public void DeserializeResult_AllowsExplicitNullForOptionalToolResult()
    {
        var result = McpServerConnection.DeserializeResult<ConsistencyValidationEvidence?>(
            "null",
            "vc_validation_get",
            "versioncontrol");

        Assert.Null(result);
    }
}
