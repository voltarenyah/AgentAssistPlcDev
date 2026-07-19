using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace App;

/// <summary>
/// Resolves MCP server exe paths and DeepSeek API settings (buildnote/plan/app.md §2.6, agent.md rule 4).
/// Overrides live in %APPDATA%\PlcAiAssistant\config.json (keys: engineeringServerPath, knowledgeServerPath,
/// deepSeekApiKey, deepSeekModel, deepSeekBaseUrl); server-path defaults follow the solution layout
/// (src/&lt;server&gt;/bin/&lt;Debug|Release&gt;/&lt;tfm&gt;/&lt;server&gt;.exe).
/// The per-project export-root convention moved to Agent.AssistantPaths when step 6 landed.
/// </summary>
public sealed class AppSettings
{
    private const string SolutionFileName = "AgentAssistPlcDev.sln";

    private const string DefaultDeepSeekModel = "deepseek-v4-flash";
    private const string DefaultDeepSeekBaseUrl = "https://api.deepseek.com";
    private const string DefaultDeepSeekReasoningEffort = "high";

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    public AppSettings(
        string engineeringServerPath,
        string knowledgeServerPath,
        string? deepSeekApiKey = null,
        string? deepSeekModel = null,
        string? deepSeekBaseUrl = null,
        bool deepSeekThinkingEnabled = true,
        string? deepSeekReasoningEffort = null,
        double deepSeekTemperature = 1.0,
        double deepSeekTopP = 1.0)
    {
        EngineeringServerPath = engineeringServerPath;
        KnowledgeServerPath = knowledgeServerPath;
        DeepSeekApiKey = string.IsNullOrWhiteSpace(deepSeekApiKey) ? null : deepSeekApiKey;
        DeepSeekModel = string.IsNullOrWhiteSpace(deepSeekModel) ? DefaultDeepSeekModel : deepSeekModel!;
        DeepSeekBaseUrl = string.IsNullOrWhiteSpace(deepSeekBaseUrl) ? DefaultDeepSeekBaseUrl : deepSeekBaseUrl!;
        DeepSeekThinkingEnabled = deepSeekThinkingEnabled;
        DeepSeekReasoningEffort = string.IsNullOrWhiteSpace(deepSeekReasoningEffort) ? DefaultDeepSeekReasoningEffort : deepSeekReasoningEffort!;
        DeepSeekTemperature = deepSeekTemperature;
        DeepSeekTopP = deepSeekTopP;
    }

    public string EngineeringServerPath { get; }

    public string KnowledgeServerPath { get; }

    /// <summary>DeepSeek API key from config.json; null when not configured yet (first-run setup shows).</summary>
    public string? DeepSeekApiKey { get; }

    public string DeepSeekModel { get; }

    public string DeepSeekBaseUrl { get; }

    /// <summary>thinking: enabled/disabled (api-docs.deepseek.com/guides/thinking_mode; default enabled).</summary>
    public bool DeepSeekThinkingEnabled { get; }

    /// <summary>"high" | "max" — only relevant when thinking is enabled.</summary>
    public string DeepSeekReasoningEffort { get; }

    /// <summary>Only effective when thinking is disabled (the API ignores it in thinking mode).</summary>
    public double DeepSeekTemperature { get; }

    /// <summary>Only effective when thinking is disabled (the API ignores it in thinking mode).</summary>
    public double DeepSeekTopP { get; }

    public bool HasDeepSeekApiKey => DeepSeekApiKey != null;

    public static string ConfigFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PlcAiAssistant",
        "config.json");

    public static AppSettings Load() => Load(ConfigFilePath, AppContext.BaseDirectory);

    public static AppSettings Load(string configPath, string baseDirectory)
    {
        var overrides = ReadOverrides(configPath);

        string engineeringPath;
        string knowledgePath;
        if (overrides.EngineeringServerPath != null && overrides.KnowledgeServerPath != null)
        {
            engineeringPath = overrides.EngineeringServerPath;
            knowledgePath = overrides.KnowledgeServerPath;
        }
        else
        {
            var solutionDirectory = FindSolutionDirectory(baseDirectory)
                ?? throw new InvalidOperationException(
                    $"Could not locate {SolutionFileName} above '{baseDirectory}', and config.json overrides are incomplete. Set engineeringServerPath and knowledgeServerPath in {ConfigFilePath}.");
            engineeringPath = overrides.EngineeringServerPath
                ?? Path.Combine(solutionDirectory, "src", "Mcp.Engineering", "bin", BuildConfiguration, "net48", "Mcp.Engineering.exe");
            knowledgePath = overrides.KnowledgeServerPath
                ?? Path.Combine(solutionDirectory, "src", "Mcp.Knowledge", "bin", BuildConfiguration, "net8.0", "Mcp.Knowledge.exe");
        }

        return new AppSettings(
            engineeringPath,
            knowledgePath,
            overrides.DeepSeekApiKey,
            overrides.DeepSeekModel,
            overrides.DeepSeekBaseUrl,
            overrides.DeepSeekThinkingEnabled ?? true,
            overrides.DeepSeekReasoningEffort,
            overrides.DeepSeekTemperature ?? 1.0,
            overrides.DeepSeekTopP ?? 1.0);
    }

    /// <summary>Merge-writes the DeepSeek API key into config.json, preserving all existing keys.</summary>
    public static void SaveDeepSeekApiKey(string apiKey) => SaveDeepSeekApiKey(ConfigFilePath, apiKey);

    public static void SaveDeepSeekApiKey(string configPath, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key must not be empty.", nameof(apiKey));
        }

        UpdateConfig(configPath, root => root["deepSeekApiKey"] = apiKey.Trim());
    }

    /// <summary>Merge-writes the chat parameters (model/thinking/effort/temperature/top_p) into config.json.</summary>
    public static void SaveDeepSeekChatSettings(
        string model,
        bool thinkingEnabled,
        string reasoningEffort,
        double temperature,
        double topP) =>
        SaveDeepSeekChatSettings(ConfigFilePath, model, thinkingEnabled, reasoningEffort, temperature, topP);

    public static void SaveDeepSeekChatSettings(
        string configPath,
        string model,
        bool thinkingEnabled,
        string reasoningEffort,
        double temperature,
        double topP)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model must not be empty.", nameof(model));
        }

        UpdateConfig(configPath, root =>
        {
            root["deepSeekModel"] = model.Trim();
            root["deepSeekThinkingEnabled"] = thinkingEnabled;
            root["deepSeekReasoningEffort"] = reasoningEffort;
            root["deepSeekTemperature"] = temperature;
            root["deepSeekTopP"] = topP;
        });
    }

    private static void UpdateConfig(string configPath, Action<JsonObject> update)
    {
        JsonObject root;
        try
        {
            root = File.Exists(configPath)
                ? JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        update(root);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string? FindSolutionDirectory(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static ConfigOverrides ReadOverrides(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return new ConfigOverrides();
            }

            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            return new ConfigOverrides
            {
                EngineeringServerPath = GetString(root, "engineeringServerPath"),
                KnowledgeServerPath = GetString(root, "knowledgeServerPath"),
                DeepSeekApiKey = GetString(root, "deepSeekApiKey"),
                DeepSeekModel = GetString(root, "deepSeekModel"),
                DeepSeekBaseUrl = GetString(root, "deepSeekBaseUrl"),
                DeepSeekThinkingEnabled = GetBool(root, "deepSeekThinkingEnabled"),
                DeepSeekReasoningEffort = GetString(root, "deepSeekReasoningEffort"),
                DeepSeekTemperature = GetDouble(root, "deepSeekTemperature"),
                DeepSeekTopP = GetDouble(root, "deepSeekTopP"),
            };
        }
        catch (JsonException)
        {
            return new ConfigOverrides();
        }
    }

    private static string? GetString(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static bool? GetBool(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;
    }

    private static double? GetDouble(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : null;
    }

    private sealed class ConfigOverrides
    {
        public string? EngineeringServerPath { get; init; }
        public string? KnowledgeServerPath { get; init; }
        public string? DeepSeekApiKey { get; init; }
        public string? DeepSeekModel { get; init; }
        public string? DeepSeekBaseUrl { get; init; }
        public bool? DeepSeekThinkingEnabled { get; init; }
        public string? DeepSeekReasoningEffort { get; init; }
        public double? DeepSeekTemperature { get; init; }
        public double? DeepSeekTopP { get; init; }
    }
}
