namespace Agent.Workbench;

/// <summary>
/// Lists the exported-source XML files of a device so the chat runtime context can hand the model
/// exact relativePath values for the src_* tools, instead of letting it guess TIA export filenames
/// (which carry a " [FC2]"-style suffix the block name alone does not reveal).
/// </summary>
public static class SourceFileListing
{
    /// <summary>Cap for the runtime-context listing; overflow is summarized as "+N more".</summary>
    public const int MaxListed = 100;

    public static IReadOnlyList<string> List(DeviceContext device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var root = device.ExportedSourceRoot;
        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if (IsReparsePoint(directory))
            {
                continue;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (IsReparsePoint(entry))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                if (!entry.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, entry);
                _ = WorkbenchPaths.ResolveRelative(root, relative);
                paths.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
            }
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    /// <summary>Renders the runtime-context section: exact relativePath values or an empty-state hint.</summary>
    public static string Format(DeviceContext device)
    {
        var paths = List(device);
        if (paths.Count == 0)
        {
            return "Source files: (none — refresh the device export first)";
        }

        var lines = new List<string>
        {
            "Source files (pass one of these as relativePath to the src_* tools):",
        };
        lines.AddRange(paths.Take(MaxListed).Select(path => $"- {path}"));
        if (paths.Count > MaxListed)
        {
            lines.Add($"… and {paths.Count - MaxListed} more");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
