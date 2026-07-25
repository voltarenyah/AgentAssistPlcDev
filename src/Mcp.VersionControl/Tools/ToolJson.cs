using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace Mcp.VersionControl.Tools;

/// <summary>
/// Tool-result envelope, mirroring Mcp.Knowledge's convention: failures are isError=true
/// results with a structured { code, message, remediation } payload.
/// </summary>
internal static class ToolJson
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static CallToolResult Ok(object payload) => new()
    {
        IsError = false,
        Content = new List<ContentBlock>
        {
            new TextContentBlock { Text = JsonSerializer.Serialize(payload, Json) },
        },
    };

    public static CallToolResult Fail(string code, string message, string? remediation = null) => new()
    {
        IsError = true,
        Content = new List<ContentBlock>
        {
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(new { error = new { code, message, remediation } }, Json),
            },
        },
    };
}
