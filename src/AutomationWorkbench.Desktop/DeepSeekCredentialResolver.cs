using System.Text.Json;

namespace AutomationWorkbench.Desktop;

internal static class DeepSeekCredentialResolver
{
    private static readonly string[] EnvironmentNames =
    [
        "DEEPSEEK_API_KEY",
        "DeepSeek__ApiKey",
        "DeepSeek:ApiKey",
        "deepSeekApiKey",
    ];

    public static string? ResolveApiKey(IReadOnlyList<string>? configPaths = null)
    {
        foreach (var name in EnvironmentNames)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        var paths = configPaths ?? DefaultConfigPaths();
        foreach (var path in paths)
        {
            var value = ReadApiKey(path);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static IReadOnlyList<string> DefaultConfigPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return
        [
            Path.Combine(appData, "AutomationWorkbench", "config.json"),
            Path.Combine(appData, "PlcAiAssistant", "config.json"),
        ];
    }

    private static string? ReadApiKey(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("deepSeekApiKey", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("DeepSeek:ApiKey", StringComparison.OrdinalIgnoreCase))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;

                if (property.Name.Equals("DeepSeek", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("ApiKey", out var nested)
                    && nested.ValueKind == JsonValueKind.String)
                    return nested.GetString();
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }
}
