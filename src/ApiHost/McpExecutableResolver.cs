public sealed record McpExecutablePaths(
    string Engineering, string Knowledge, string VersionControl, string SourceEditor);

public static class McpExecutableResolver
{
    public static McpExecutablePaths Resolve(IConfiguration configuration, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var solution = FindSolutionDirectory(baseDirectory);
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        return new(
            Value(configuration, "Mcp:Engineering", "engineeringServerPath")
                ?? Path.Combine(solution, "src", "Mcp.Engineering", "bin", build, "net48", "Mcp.Engineering.exe"),
            Value(configuration, "Mcp:Knowledge", "knowledgeServerPath")
                ?? Path.Combine(solution, "src", "Mcp.Knowledge", "bin", build, "net8.0", "Mcp.Knowledge.exe"),
            Value(configuration, "Mcp:VersionControl", "versionControlServerPath")
                ?? Path.Combine(solution, "src", "Mcp.VersionControl", "bin", build, "net8.0", "Mcp.VersionControl.exe"),
            Value(configuration, "Mcp:SourceEditor", "sourceEditorServerPath")
                ?? Path.Combine(solution, "src", "Mcp.SourceEditor", "bin", build, "net8.0", "Mcp.SourceEditor.exe"));
    }

    private static string? Value(IConfiguration configuration, string current, string legacy) =>
        configuration[current] ?? configuration[legacy];

    private static string FindSolutionDirectory(string start)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(start));
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentAssistPlcDev.sln")))
                return directory.FullName;
        }
        throw new InvalidOperationException($"Could not locate AgentAssistPlcDev.sln above '{start}'.");
    }
}
