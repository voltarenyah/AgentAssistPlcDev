public sealed record McpExecutablePaths(
    string Engineering, string Knowledge, string VersionControl, string SourceEditor);

public static class McpExecutableResolver
{
    private static readonly (string Name, string RelativePath)[] InstalledExecutables =
    [
        ("Engineering", Path.Combine("mcp", "engineering", "Mcp.Engineering.exe")),
        ("Knowledge", Path.Combine("mcp", "knowledge", "Mcp.Knowledge.exe")),
        ("SourceEditor", Path.Combine("mcp", "source-editor", "Mcp.SourceEditor.exe")),
        ("VersionControl", Path.Combine("mcp", "version-control", "Mcp.VersionControl.exe")),
    ];

    public static McpExecutablePaths Resolve(IConfiguration configuration, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var executableDirectory = Path.GetFullPath(baseDirectory);
        var solution = FindSolutionDirectory(executableDirectory);
        var installedLayoutDetected = InstalledExecutables.Any(executable =>
            File.Exists(Path.Combine(executableDirectory, executable.RelativePath)));
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        return new(
            ResolveExecutable(
                configuration, "Mcp:Engineering", "engineeringServerPath", executableDirectory,
                InstalledExecutables[0].RelativePath, installedLayoutDetected, solution,
                Path.Combine("src", "Mcp.Engineering", "bin", build, "net48", "Mcp.Engineering.exe")),
            ResolveExecutable(
                configuration, "Mcp:Knowledge", "knowledgeServerPath", executableDirectory,
                InstalledExecutables[1].RelativePath, installedLayoutDetected, solution,
                Path.Combine("src", "Mcp.Knowledge", "bin", build, "net8.0", "Mcp.Knowledge.exe")),
            ResolveExecutable(
                configuration, "Mcp:VersionControl", "versionControlServerPath", executableDirectory,
                InstalledExecutables[3].RelativePath, installedLayoutDetected, solution,
                Path.Combine("src", "Mcp.VersionControl", "bin", build, "net8.0", "Mcp.VersionControl.exe")),
            ResolveExecutable(
                configuration, "Mcp:SourceEditor", "sourceEditorServerPath", executableDirectory,
                InstalledExecutables[2].RelativePath, installedLayoutDetected, solution,
                Path.Combine("src", "Mcp.SourceEditor", "bin", build, "net8.0", "Mcp.SourceEditor.exe")));
    }

    public static void Validate(McpExecutablePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var missing = new List<string>();

        AddMissing(missing, "Engineering", paths.Engineering);
        AddMissing(missing, "Knowledge", paths.Knowledge);
        AddMissing(missing, "SourceEditor", paths.SourceEditor);
        AddMissing(missing, "VersionControl", paths.VersionControl);

        if (missing.Count == 0)
            return;

        throw new InvalidOperationException($"""
            Required MCP executables were not found:

            {string.Join(Environment.NewLine, missing)}

            Repair the installation or configure the corresponding Mcp path.
            """);
    }

    private static string? Value(IConfiguration configuration, string current, string legacy) =>
        configuration[current] ?? configuration[legacy];

    private static string ResolveExecutable(
        IConfiguration configuration,
        string currentKey,
        string legacyKey,
        string executableDirectory,
        string installedRelativePath,
        bool installedLayoutDetected,
        string? solutionDirectory,
        string developmentRelativePath)
    {
        var configured = Value(configuration, currentKey, legacyKey);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(configured, executableDirectory);
        }

        var installedPath = Path.Combine(executableDirectory, installedRelativePath);
        if (installedLayoutDetected || solutionDirectory is null)
            return installedPath;

        return Path.Combine(solutionDirectory, developmentRelativePath);
    }

    private static void AddMissing(ICollection<string> missing, string name, string path)
    {
        if (!File.Exists(path))
            missing.Add($"{name}: {path}");
    }

    private static string? FindSolutionDirectory(string start)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(start));
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentAssistPlcDev.sln")))
                return directory.FullName;
        }
        return null;
    }
}
