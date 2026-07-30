namespace Agent.Workbench;

public sealed class WorkbenchPathException : Exception
{
    public WorkbenchPathException(string message)
        : base(message)
    {
    }

    public WorkbenchPathException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class WorkbenchPaths
{
    public static string DefaultRoot(string name)
    {
        var safeName = SanitizeDirectoryName(name);
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        return Canonicalize(
            Path.Combine(localApplicationData, "AutomationWorkbench", "Project", safeName));
    }

    public static string ResolveWorkbench(string name, string? requestedRoot = null)
    {
        _ = SanitizeDirectoryName(name);

        if (requestedRoot is null)
        {
            return DefaultRoot(name);
        }

        if (string.IsNullOrWhiteSpace(requestedRoot))
        {
            throw new WorkbenchPathException("A custom workbench root cannot be blank.");
        }

        var root = Canonicalize(requestedRoot);
        RejectExistingReparsePoints(root);
        return root;
    }

    public static string ResolveWorktree(string workbenchRoot, string relativePath)
    {
        var root = Canonicalize(workbenchRoot);
        RejectExistingReparsePoints(root);

        return ResolveRelative(Path.Combine(root, "worktrees"), relativePath);
    }

    public static DeviceContext ResolveDevice(
        string workbenchRoot,
        string worktreeId,
        string worktreeRelativePath,
        string deviceId,
        string deviceName) =>
        ResolveDevice(
            string.Empty,
            workbenchRoot,
            worktreeId,
            worktreeRelativePath,
            deviceId,
            deviceName);

    public static DeviceContext ResolveDevice(
        string workbenchId,
        string workbenchRoot,
        string worktreeId,
        string worktreeRelativePath,
        string deviceId,
        string deviceName)
    {
        ValidateId(worktreeId, nameof(worktreeId));
        ValidateId(deviceId, nameof(deviceId));

        var root = Canonicalize(workbenchRoot);
        var worktreeRoot = ResolveWorktree(root, worktreeRelativePath);
        var deviceRoot = ResolveRelative(
            Path.Combine(worktreeRoot, "devices"),
            SanitizeDirectoryName(deviceName));

        return new DeviceContext(
            workbenchId,
            worktreeId,
            deviceId,
            root,
            worktreeRoot,
            deviceRoot,
            ResolveRelative(deviceRoot, "exported-source"),
            ResolveRelative(deviceRoot, "modified-source"),
            ResolveRelative(deviceRoot, "staging"),
            ResolveRelative(deviceRoot, "plc-knowledge.db"));
    }

    public static string ResolveRelative(string parentRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(parentRoot))
        {
            throw new WorkbenchPathException("A parent root is required.");
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new WorkbenchPathException("A relative path is required.");
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new WorkbenchPathException($"The path '{relativePath}' must be relative.");
        }

        var parent = Canonicalize(parentRoot);
        string resolved;

        try
        {
            resolved = Path.GetFullPath(relativePath, parent);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new WorkbenchPathException($"The path '{relativePath}' is invalid.", exception);
        }

        var relativeToParent = Path.GetRelativePath(parent, resolved);
        if (relativeToParent == "."
            || Path.IsPathRooted(relativeToParent)
            || relativeToParent == ".."
            || relativeToParent.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            || relativeToParent.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison))
        {
            throw new WorkbenchPathException(
                $"The path '{relativePath}' resolves outside its parent root.");
        }

        RejectExistingReparsePoints(parent);
        RejectExistingReparsePoints(resolved);
        return resolved;
    }

    /// <summary>
    /// Canonicalizes a root once for hot loops and rejects reparse points along it. Pair with
    /// <see cref="ResolveRelativeBelowValidatedRoot"/> so per-item resolution does not re-walk
    /// the same deep root (a full walk per item costs ~30 file attribute queries on workbench
    /// depth paths — quadratic over hundreds of manifest components).
    /// </summary>
    public static string ValidateResolvedRoot(string root)
    {
        var canonical = Canonicalize(root);
        RejectExistingReparsePoints(canonical);
        return canonical;
    }

    /// <summary>
    /// <see cref="ResolveRelative"/> variant for roots already returned by
    /// <see cref="ValidateResolvedRoot"/>: the same containment rules apply, but only the
    /// segments below the validated root are reparse-checked.
    /// </summary>
    public static string ResolveRelativeBelowValidatedRoot(string validatedRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new WorkbenchPathException("A relative path is required.");
        }

        if (Path.IsPathRooted(relativePath))
        {
            throw new WorkbenchPathException($"The path '{relativePath}' must be relative.");
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(relativePath, validatedRoot);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new WorkbenchPathException($"The path '{relativePath}' is invalid.", exception);
        }

        var relativeToParent = Path.GetRelativePath(validatedRoot, resolved);
        if (relativeToParent == "."
            || Path.IsPathRooted(relativeToParent)
            || relativeToParent == ".."
            || relativeToParent.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            || relativeToParent.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison))
        {
            throw new WorkbenchPathException(
                $"The path '{relativePath}' resolves outside its parent root.");
        }

        RejectReparsePointsBelowRoot(validatedRoot, resolved, relativePath);
        return resolved;
    }

    private static void RejectReparsePointsBelowRoot(string validatedRoot, string resolved, string relativePath)
    {
        var current = validatedRoot;
        foreach (var segment in Path.GetRelativePath(validatedRoot, resolved).Split(
                     Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }

            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new WorkbenchPathException(
                        $"The path '{relativePath}' traverses reparse point '{current}'.");
                }
            }
            catch (WorkbenchPathException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                throw new WorkbenchPathException(
                    $"The path segment '{current}' could not be validated.",
                    exception);
            }
        }
    }

    private static string SanitizeDirectoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            throw new WorkbenchPathException(
                "A workbench or device name cannot be blank, '.' or '..'.");
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(
            name.Select(character => invalidCharacters.Contains(character) ? '_' : character)
                .ToArray());
    }

    private static string Canonicalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            throw new WorkbenchPathException($"The path '{path}' is invalid.", exception);
        }
    }

    private static void ValidateId(string id, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new WorkbenchPathException($"{parameterName} cannot be blank.");
        }
    }

    private static void RejectExistingReparsePoints(string path)
    {
        var canonicalPath = Canonicalize(path);
        var root = Path.GetPathRoot(canonicalPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new WorkbenchPathException($"The path '{path}' has no filesystem root.");
        }

        var current = root;
        foreach (var segment in canonicalPath[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }

            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new WorkbenchPathException(
                        $"The path '{path}' traverses reparse point '{current}'.");
                }
            }
            catch (WorkbenchPathException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                throw new WorkbenchPathException(
                    $"The path segment '{current}' could not be validated.",
                    exception);
            }
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
