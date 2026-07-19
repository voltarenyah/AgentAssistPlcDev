using System.IO;
using System.Text.Json;

namespace App;

/// <summary>
/// Resolves MCP server exe paths and the per-project export root (buildnote/plan/app.md §2.5/§2.6).
/// Overrides live in %APPDATA%\PlcAiAssistant\config.json (keys: engineeringServerPath, knowledgeServerPath);
/// defaults follow the solution layout (src/&lt;server&gt;/bin/&lt;Debug|Release&gt;/&lt;tfm&gt;/&lt;server&gt;.exe).
/// </summary>
public sealed class AppSettings
{
    private const string SolutionFileName = "AgentAssistPlcDev.sln";

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    public AppSettings(string engineeringServerPath, string knowledgeServerPath)
    {
        EngineeringServerPath = engineeringServerPath;
        KnowledgeServerPath = knowledgeServerPath;
    }

    public string EngineeringServerPath { get; }

    public string KnowledgeServerPath { get; }

    public static string ConfigFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PlcAiAssistant",
        "config.json");

    public static AppSettings Load() => Load(ConfigFilePath, AppContext.BaseDirectory);

    public static AppSettings Load(string configPath, string baseDirectory)
    {
        var (engineeringOverride, knowledgeOverride) = ReadOverrides(configPath);
        if (engineeringOverride != null && knowledgeOverride != null)
        {
            return new AppSettings(engineeringOverride, knowledgeOverride);
        }

        var solutionDirectory = FindSolutionDirectory(baseDirectory)
            ?? throw new InvalidOperationException(
                $"Could not locate {SolutionFileName} above '{baseDirectory}', and config.json overrides are incomplete. Set engineeringServerPath and knowledgeServerPath in {ConfigFilePath}.");

        return new AppSettings(
            engineeringOverride ?? Path.Combine(solutionDirectory, "src", "Mcp.Engineering", "bin", BuildConfiguration, "net48", "Mcp.Engineering.exe"),
            knowledgeOverride ?? Path.Combine(solutionDirectory, "src", "Mcp.Knowledge", "bin", BuildConfiguration, "net8.0", "Mcp.Knowledge.exe"));
    }

    /// <summary>Per-project export root: %LOCALAPPDATA%\PlcAiAssistant\exports\&lt;projectName&gt; (invalid chars → '_').</summary>
    public static string ResolveExportRoot(string projectName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(projectName.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlcAiAssistant",
            "exports",
            sanitized);
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

    private static (string? Engineering, string? Knowledge) ReadOverrides(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return (null, null);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            return (GetString(root, "engineeringServerPath"), GetString(root, "knowledgeServerPath"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? GetString(JsonElement root, string property)
    {
        return root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }
}
