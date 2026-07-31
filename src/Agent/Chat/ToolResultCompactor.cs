using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Chat;

/// <summary>Bounds tool feedback without cutting JSON in the middle of a token or value.</summary>
internal static class ToolResultCompactor
{
    public static string Compact(JsonElement result, int maxChars)
    {
        var raw = result.GetRawText();
        if (raw.Length <= maxChars)
        {
            return raw;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = "TOOL_RESULT_INVALID_JSON",
                    message = "The MCP server returned invalid JSON.",
                    retryable = false,
                },
            });
        }

        var truncated = false;
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToArray())
            {
                if (property.Value is JsonValue value && TryGetString(value, out var text) && text.Length > 1200)
                {
                    obj[property.Key] = text[..1200] + "...";
                    truncated = true;
                }
            }

            foreach (var array in obj.Select(item => item.Value).OfType<JsonArray>())
            {
                while (obj.ToJsonString().Length > maxChars && array.Count() > 1)
                {
                    array.RemoveAt(array.Count() - 1);
                    truncated = true;
                }
            }

            while (obj.ToJsonString().Length > maxChars)
            {
                var largest = obj
                    .Where(item => item.Value is JsonValue value && TryGetString(value, out _))
                    .OrderByDescending(item => item.Value!.ToJsonString().Length)
                    .FirstOrDefault();
                if (largest.Key == null || largest.Value is not JsonValue largestValue || !TryGetString(largestValue, out var largestText))
                {
                    break;
                }

                var keep = Math.Max(64, largestText.Length - Math.Max(64, obj.ToJsonString().Length - maxChars));
                obj[largest.Key] = largestText[..Math.Min(keep, largestText.Length)] + "...";
                truncated = true;
            }

            obj["_truncated"] = truncated;
            var compactedObject = obj.ToJsonString();
            return compactedObject.Length <= maxChars
                ? compactedObject
                : MinimalSummary("object", maxChars, obj.Select(item => item.Key));
        }

        if (node is JsonArray rootArray)
        {
            var originalCount = rootArray.Count();
            while (rootArray.ToJsonString().Length > maxChars && rootArray.Count() > 1)
            {
                rootArray.RemoveAt(rootArray.Count() - 1);
            }

            var compactedArray = new JsonObject
            {
                ["items"] = rootArray,
                ["_truncated"] = rootArray.Count() < originalCount,
                ["_omitted"] = originalCount - rootArray.Count(),
            }.ToJsonString();
            return compactedArray.Length <= maxChars
                ? compactedArray
                : MinimalSummary("array", maxChars, Array.Empty<string>());
        }

        var scalar = new JsonObject
        {
            ["value"] = node,
            ["_truncated"] = true,
        }.ToJsonString();
        return scalar.Length <= maxChars
            ? scalar
            : MinimalSummary("value", maxChars, Array.Empty<string>());
    }

    private static string MinimalSummary(string kind, int maxChars, IEnumerable<string> keys)
    {
        var summary = new JsonObject
        {
            ["_truncated"] = true,
            ["_omitted"] = true,
            ["originalType"] = kind,
            ["availableFields"] = new JsonArray(keys.Select(key => JsonValue.Create(key)).ToArray()),
        }.ToJsonString();
        return summary.Length <= maxChars ? summary : "{\"_truncated\":true}";
    }

    private static bool TryGetString(JsonValue value, out string text)
    {
        try
        {
            text = value.GetValue<string>();
            return true;
        }
        catch (InvalidOperationException)
        {
            text = string.Empty;
            return false;
        }
    }
}
