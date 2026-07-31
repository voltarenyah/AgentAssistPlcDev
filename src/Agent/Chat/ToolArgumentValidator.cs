using System.Text.Json;

namespace Agent.Chat;

/// <summary>Validates the common subset of MCP JSON schemas before a call reaches a server.</summary>
internal static class ToolArgumentValidator
{
    public static string? Validate(JsonElement schema, JsonElement arguments)
    {
        if (schema.ValueKind != JsonValueKind.Object || arguments.ValueKind != JsonValueKind.Object)
        {
            return "Tool arguments must be a JSON object.";
        }

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } name || name == "dbPath")
                {
                    continue;
                }

                if (!arguments.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    return $"Missing required argument '{name}'.";
                }
            }
        }

        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var argument in arguments.EnumerateObject())
        {
            if (!properties.TryGetProperty(argument.Name, out var propertySchema) || propertySchema.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (propertySchema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                !MatchesType(argument.Value, type.GetString()!))
            {
                return $"Argument '{argument.Name}' must be {type.GetString()}.";
            }

            if (argument.Value.ValueKind == JsonValueKind.String &&
                propertySchema.TryGetProperty("minLength", out var minLength) &&
                minLength.TryGetInt32(out var minimumLength) && argument.Value.GetString()!.Length < minimumLength)
            {
                return $"Argument '{argument.Name}' must contain at least {minimumLength} characters.";
            }

            if (argument.Value.ValueKind == JsonValueKind.Number &&
                propertySchema.TryGetProperty("minimum", out var minimum) &&
                minimum.TryGetDecimal(out var minimumValue) && argument.Value.GetDecimal() < minimumValue)
            {
                return $"Argument '{argument.Name}' must be at least {minimumValue}.";
            }

            if (propertySchema.TryGetProperty("enum", out var choices) && choices.ValueKind == JsonValueKind.Array &&
                !choices.EnumerateArray().Any(choice => choice.ValueKind == argument.Value.ValueKind && choice.GetRawText() == argument.Value.GetRawText()))
            {
                return $"Argument '{argument.Name}' has an unsupported value.";
            }
        }

        return null;
    }

    private static bool MatchesType(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };
}
