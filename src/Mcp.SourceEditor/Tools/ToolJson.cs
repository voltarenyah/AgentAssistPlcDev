using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace Mcp.SourceEditor.Tools;

internal static class ToolJson
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static CallToolResult Ok(object payload) => new()
    {
        IsError = false,
        Content = new List<ContentBlock> { new TextContentBlock { Text = JsonSerializer.Serialize(payload, Json) } },
    };

    public static CallToolResult Fail(string code, string message, string? remediation = null, int? batchIndex = null) => new()
    {
        IsError = true,
        Content = new List<ContentBlock>
        {
            new TextContentBlock { Text = JsonSerializer.Serialize(new { error = new { code, message, remediation, batchIndex } }, Json) },
        },
    };
}
