namespace Agent;

/// <summary>Shared per-project path conventions of the assistant (export roots, knowledge db).</summary>
public static class AssistantPaths
{
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

    /// <summary>Default knowledge db for a project: &lt;exportRoot&gt;\plc-knowledge.db.</summary>
    public static string ResolveKnowledgeDbPath(string projectName) =>
        Path.Combine(ResolveExportRoot(projectName), "plc-knowledge.db");
}
