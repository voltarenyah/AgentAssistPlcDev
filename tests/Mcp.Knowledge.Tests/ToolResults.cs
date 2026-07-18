using System.Linq;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Mcp.Knowledge.Tests;

internal static class ToolResults
{
    public static JsonElement OkJson(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        return Parse(result);
    }

    public static JsonElement ErrorJson(CallToolResult result)
    {
        Assert.Equal(true, result.IsError);
        return Parse(result).GetProperty("error");
    }

    private static JsonElement Parse(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(result.Content.Single()).Text;
        return JsonDocument.Parse(text).RootElement;
    }
}
