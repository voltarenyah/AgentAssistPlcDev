namespace Contracts.Sandbox;

/// <summary>
/// Filesystem jail for path arguments (outputDir, xmlFilePath, projectPath, …): canonicalizes
/// the path (collapsing "..") and rejects anything outside the configured roots or on the
/// network (UNC). Roots are compared case-insensitively (Windows).
/// </summary>
public sealed class PathJail
{
    private readonly List<string> roots;
    private readonly TrustedWorkbenchRootRegistry? trustedRoots;

    public PathJail(IEnumerable<string> roots, string? trustedWorkbenchRootsFile = null)
    {
        this.roots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(CanonicalRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        trustedRoots = string.IsNullOrWhiteSpace(trustedWorkbenchRootsFile)
            ? null
            : new TrustedWorkbenchRootRegistry(trustedWorkbenchRootsFile);
    }

    public IReadOnlyList<string> Roots => EffectiveRoots();

    /// <summary>Returns the canonical path when inside a root; throws <see cref="SandboxException"/> otherwise.</summary>
    public string Validate(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new SandboxException("SANDBOX_PATH_DENIED", $"{parameterName}: path must not be empty.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex)
        {
            throw new SandboxException("SANDBOX_PATH_DENIED", $"{parameterName}: invalid path — {ex.Message}");
        }

        if (new Uri(full).IsUnc)
        {
            throw new SandboxException(
                "SANDBOX_PATH_DENIED",
                $"{parameterName}: network paths are not allowed ({full}).",
                "Copy the files to a local directory inside an allowed root.");
        }

        // Directory links (junctions) are resolved and the REAL target is validated against the
        // roots: a link can never widen the sandbox, but tools that cannot handle long paths may
        // work through a short alias that points inside a root. Links with an unreadable target,
        // or whose target escapes every root, are rejected.
        var checkPath = ResolveDirectoryLinks(full, parameterName);
        var effectiveRoots = EffectiveRoots();
        foreach (var root in effectiveRoots)
        {
            if (checkPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || string.Equals(checkPath, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }
        }

        throw new SandboxException(
            "SANDBOX_PATH_DENIED",
            $"{parameterName}: '{full}' is outside the sandbox roots ({string.Join("; ", effectiveRoots)}).",
            $"Add the directory to allowedRoots in {SandboxConfig.DefaultFilePath}.");
    }

    private IReadOnlyList<string> EffectiveRoots() => roots
        .Concat(trustedRoots?.Read().Select(CanonicalRoot) ?? Enumerable.Empty<string>())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>Canonical root with a trailing separator, so "C:\roots" cannot match "C:\roots-eve".</summary>
    private static string CanonicalRoot(string root)
    {
        var full = Path.GetFullPath(root.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full + Path.DirectorySeparatorChar;
    }

    /// <summary>Walks every path segment; for each existing directory reparse point (junction /
    /// directory symlink) the walk continues from the link's real target, yielding the fully
    /// resolved location. Unresolvable reparse points are rejected outright.</summary>
    private static string ResolveDirectoryLinks(string full, string parameterName)
    {
        var root = Path.GetPathRoot(full)!;
        var relative = full.Substring(root.Length);
        var current = root;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            var target = TryReadLinkTarget(current);
            if (target is null)
            {
                throw new SandboxException(
                    "SANDBOX_PATH_DENIED",
                    $"{parameterName}: path '{full}' traverses reparse point '{current}' whose target cannot be resolved.");
            }

            current = Path.GetFullPath(target);
        }

        return current;
    }

    // Junction target resolution for netstandard2.0/net48 (no FileSystemInfo.LinkTarget):
    // open the reparse point itself and ask the kernel for its final path.
    private static string? TryReadLinkTarget(string path)
    {
        if (!OperatingSystemIsWindows())
        {
            return null;
        }

        var handle = CreateFile(
            path,
            GenericRead,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            // Follow the link: the kernel resolves the junction and the final path name is the
            // real target. (Opening with FILE_FLAG_OPEN_REPARSE_POINT would return the link's
            // own path instead.)
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle == InvalidHandle)
        {
            return null;
        }

        try
        {
            var buffer = new System.Text.StringBuilder(1024);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, VolumeNameDos);
            if (length == 0 || length >= buffer.Capacity)
            {
                return null;
            }

            var resolved = buffer.ToString();
            const string prefix = @"\\?\";
            return resolved.StartsWith(prefix, StringComparison.Ordinal) ? resolved.Substring(prefix.Length) : resolved;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool OperatingSystemIsWindows() =>
        Environment.OSVersion.Platform == PlatformID.Win32NT;

    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint VolumeNameDos = 0x0;
    private static readonly IntPtr InvalidHandle = new(-1);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        IntPtr hFile,
        System.Text.StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
