namespace Agent.Workbench;

public sealed class DeviceSourceResolver
{
    /// <summary>Suffix of disposable preview outputs next to an overlay; never imported or listed as an overlay.</summary>
    public const string PreviewFileSuffix = ".preview.xml";

    public static bool IsPreviewFile(string path) =>
        path.EndsWith(PreviewFileSuffix, StringComparison.OrdinalIgnoreCase);

    private readonly Action<DeviceContext> markKnowledgeStale;

    public DeviceSourceResolver(Action<DeviceContext> markKnowledgeStale)
    {
        this.markKnowledgeStale = markKnowledgeStale
            ?? throw new ArgumentNullException(nameof(markKnowledgeStale));
    }

    public string ResolveEffective(DeviceContext context, string relativePath)
    {
        var roots = ValidateRoots(context);
        var modifiedPath = WorkbenchPaths.ResolveRelative(
            roots.ModifiedSourceRoot,
            relativePath);

        return File.Exists(modifiedPath)
            ? modifiedPath
            : WorkbenchPaths.ResolveRelative(
                roots.ExportedSourceRoot,
                relativePath);
    }

    public string PrepareEditable(DeviceContext context, string relativePath)
    {
        var roots = ValidateRoots(context);
        var baselinePath = WorkbenchPaths.ResolveRelative(
            roots.ExportedSourceRoot,
            relativePath);
        var modifiedPath = WorkbenchPaths.ResolveRelative(
            roots.ModifiedSourceRoot,
            relativePath);

        if (File.Exists(modifiedPath))
        {
            markKnowledgeStale(context);
            return modifiedPath;
        }

        if (Directory.Exists(modifiedPath))
        {
            throw new IOException(
                $"The modified-source path is a directory: {modifiedPath}");
        }

        if (!File.Exists(baselinePath))
        {
            throw new FileNotFoundException(
                $"The exported source file does not exist: {baselinePath}",
                baselinePath);
        }

        var outputDirectory = Path.GetDirectoryName(modifiedPath)
            ?? throw new IOException(
                $"The modified-source path has no parent directory: {modifiedPath}");
        Directory.CreateDirectory(outputDirectory);

        // Validate again after creating the directory hierarchy so an existing
        // reparse point can never become part of the copy destination.
        modifiedPath = WorkbenchPaths.ResolveRelative(
            roots.ModifiedSourceRoot,
            relativePath);

        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(modifiedPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var input = new FileStream(
                       baselinePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.WriteThrough))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, modifiedPath);
            }
            catch (IOException) when (File.Exists(modifiedPath))
            {
                // Another editor won the copy-on-write race. Preserve its
                // overlay and treat the path as prepared.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        markKnowledgeStale(context);
        return modifiedPath;
    }

    public string CreateNew(
        DeviceContext context,
        string relativePath,
        ReadOnlySpan<byte> initialContent)
    {
        var modifiedSourceRoot = ValidateRoots(context).ModifiedSourceRoot;
        var modifiedPath = WorkbenchPaths.ResolveRelative(
            modifiedSourceRoot,
            relativePath);

        if (File.Exists(modifiedPath) || Directory.Exists(modifiedPath))
        {
            throw new IOException(
                $"The modified-source path already exists: {modifiedPath}");
        }

        var outputDirectory = Path.GetDirectoryName(modifiedPath)
            ?? throw new IOException(
                $"The modified-source path has no parent directory: {modifiedPath}");
        Directory.CreateDirectory(outputDirectory);

        // Validate the newly created hierarchy before placing any content in it.
        modifiedPath = WorkbenchPaths.ResolveRelative(
            modifiedSourceRoot,
            relativePath);

        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(modifiedPath)}.{Guid.NewGuid():N}.tmp");

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

            File.Move(temporaryPath, modifiedPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        markKnowledgeStale(context);
        return modifiedPath;
    }

    public IReadOnlyList<string> EnumerateModified(DeviceContext context)
    {
        var modifiedSourceRoot = ValidateRoots(context).ModifiedSourceRoot;
        if (!Directory.Exists(modifiedSourceRoot))
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(modifiedSourceRoot);

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

                if (!File.Exists(entry) || IsPreviewFile(entry))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(
                    modifiedSourceRoot,
                    entry);
                _ = WorkbenchPaths.ResolveRelative(
                    modifiedSourceRoot,
                    relativePath);
                paths.Add(
                    relativePath.Replace(
                        Path.DirectorySeparatorChar,
                        '/'));
            }
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private static SourceRoots ValidateRoots(DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var exportedSourceRoot = ValidateDeclaredRoot(
            context.DeviceRoot,
            context.ExportedSourceRoot,
            "exported-source");
        var modifiedSourceRoot = ValidateDeclaredRoot(
            context.DeviceRoot,
            context.ModifiedSourceRoot,
            "modified-source");

        if (string.Equals(
                exportedSourceRoot,
                modifiedSourceRoot,
                PathComparison))
        {
            throw new WorkbenchPathException(
                "Exported and modified source roots must be distinct.");
        }

        return new SourceRoots(exportedSourceRoot, modifiedSourceRoot);
    }

    private static string ValidateDeclaredRoot(
        string deviceRoot,
        string declaredRoot,
        string expectedDirectoryName)
    {
        var expectedRoot = WorkbenchPaths.ResolveRelative(
            deviceRoot,
            expectedDirectoryName);
        var canonicalDeclaredRoot = Path.GetFullPath(declaredRoot);

        if (!string.Equals(
                expectedRoot,
                canonicalDeclaredRoot,
                PathComparison))
        {
            throw new WorkbenchPathException(
                $"The declared {expectedDirectoryName} root is outside the device context.");
        }

        return expectedRoot;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new WorkbenchPathException(
                $"The modified-source tree traverses reparse point '{path}'.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record SourceRoots(
        string ExportedSourceRoot,
        string ModifiedSourceRoot);
}
