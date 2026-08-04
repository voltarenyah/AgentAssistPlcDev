namespace Agent.Workbench;

public sealed class DeviceSourceResolver
{
    private readonly Action<DeviceContext> markKnowledgeStale;

    public DeviceSourceResolver(Action<DeviceContext> markKnowledgeStale)
    {
        this.markKnowledgeStale = markKnowledgeStale
            ?? throw new ArgumentNullException(nameof(markKnowledgeStale));
    }

    public string ResolveEffective(DeviceContext context, string relativePath)
    {
        var sourcePath = ResolveSourcePath(context, relativePath);
        RequireExistingFile(sourcePath);
        return sourcePath;
    }

    public string PrepareEditable(DeviceContext context, string relativePath)
    {
        var sourcePath = ResolveSourcePath(context, relativePath);
        RequireExistingFile(sourcePath);
        markKnowledgeStale(context);
        return sourcePath;
    }

    public string CreateNew(
        DeviceContext context,
        string relativePath,
        ReadOnlySpan<byte> initialContent)
    {
        var sourceRoot = ValidateSourceRoot(context);
        var sourcePath = WorkbenchPaths.ResolveRelative(sourceRoot, relativePath);

        if (File.Exists(sourcePath) || Directory.Exists(sourcePath))
        {
            throw new IOException($"The source path already exists: {sourcePath}");
        }

        var outputDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new IOException($"The source path has no parent directory: {sourcePath}");
        Directory.CreateDirectory(outputDirectory);

        // Validate the newly created hierarchy before placing any content in it.
        sourcePath = WorkbenchPaths.ResolveRelative(sourceRoot, relativePath);

        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(sourcePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.WriteThrough))
            {
                output.Write(initialContent);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, sourcePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        markKnowledgeStale(context);
        return sourcePath;
    }

    public IReadOnlyList<string> EnumerateSource(DeviceContext context)
    {
        var sourceRoot = ValidateSourceRoot(context);
        if (!Directory.Exists(sourceRoot))
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(sourceRoot);

        while (pending.TryPop(out var directory))
        {
            RejectReparsePoint(directory);

            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(entry);

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                if (!File.Exists(entry)
                    || !string.Equals(Path.GetExtension(entry), ".xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(sourceRoot, entry);
                _ = WorkbenchPaths.ResolveRelative(sourceRoot, relativePath);
                paths.Add(relativePath.Replace(Path.DirectorySeparatorChar, '/'));
            }
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private static string ResolveSourcePath(DeviceContext context, string relativePath) =>
        WorkbenchPaths.ResolveRelative(ValidateSourceRoot(context), relativePath);

    private static string ValidateSourceRoot(DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var expectedRoot = WorkbenchPaths.ResolveRelative(context.DeviceRoot, "source");
        var declaredRoot = Path.GetFullPath(context.SourceRoot);
        if (!string.Equals(expectedRoot, declaredRoot, PathComparison))
        {
            throw new WorkbenchPathException(
                "The declared source root is outside the device context.");
        }

        return expectedRoot;
    }

    private static void RequireExistingFile(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"The source file does not exist: {sourcePath}",
                sourcePath);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new WorkbenchPathException(
                $"The source tree traverses reparse point '{path}'.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
