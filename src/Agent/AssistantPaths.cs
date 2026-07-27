namespace Agent;

/// <summary>Shared per-project path conventions of the assistant (export roots, knowledge db).</summary>
/// <remarks>
/// Legacy-only compatibility for the pre-workbench storage layout. New code must
/// resolve paths through <c>Agent.Workbench.WorkbenchPaths</c>.
/// </remarks>
public static class AssistantPaths
{
    /// <summary>Per-project export root: %LOCALAPPDATA%\PlcAiAssistant\exports\&lt;projectName&gt; (invalid chars → '_').</summary>
    /// <remarks>Legacy-only; new workbenches do not write beneath this root.</remarks>
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
    /// <remarks>Legacy-only; new knowledge databases are device-scoped.</remarks>
    public static string ResolveKnowledgeDbPath(string projectName) =>
        Path.Combine(ResolveExportRoot(projectName), "plc-knowledge.db");
}
