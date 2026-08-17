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

            var probe = TryReadLinkTarget(current);
            if (probe.Target is null)
            {
                throw new SandboxException(
                    "SANDBOX_PATH_DENIED",
                    $"{parameterName}: path '{full}' traverses reparse point '{current}' whose target cannot be resolved "
                    + $"(probeFailure={probe.Failure}; win32Error={probe.Win32Error}; returnedLength={probe.ReturnedLength}).");
            }

            current = Path.GetFullPath(probe.Target);
        }

        return current;
    }

    // Junction target resolution for netstandard2.0/net48 (no FileSystemInfo.LinkTarget):
    // open the reparse point itself and read its target from the native reparse buffer. Opening
    // the link normally would follow it and recreate the legacy MAX_PATH failure for long targets.
    private static LinkTargetProbe TryReadLinkTarget(string path)
    {
        if (!OperatingSystemIsWindows())
        {
            return LinkTargetProbe.Failed("not-windows");
        }

        var handle = CreateFile(
            ToExtendedWindowsPath(path),
            GenericRead,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            // Open the link itself. Following it would reintroduce the legacy MAX_PATH failure
            // that this short alias is intended to avoid; the target is read from reparse data.
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (handle == InvalidHandle)
        {
            return LinkTargetProbe.Failed("open-failed", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }

        try
        {
            var buffer = new byte[MaximumReparseDataBufferSize];
            if (!DeviceIoControl(
                    handle,
                    FsctlGetReparsePoint,
                    IntPtr.Zero,
                    0,
                    buffer,
                    (uint)buffer.Length,
                    out var returnedLength,
                    IntPtr.Zero))
            {
                return LinkTargetProbe.Failed(
                    "reparse-data-read-failed",
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }

            if (returnedLength < ReparseHeaderSize)
            {
                return LinkTargetProbe.Failed("reparse-data-too-short", returnedLength: returnedLength);
            }

            var tag = BitConverter.ToUInt32(buffer, 0);
            var dataLength = BitConverter.ToUInt16(buffer, 4);
            var pathBufferOffset = tag switch
            {
                ReparseTagMountPoint => ReparseHeaderSize + MountPointPathFieldsSize,
                ReparseTagSymbolicLink => ReparseHeaderSize + SymbolicLinkPathFieldsSize,
                _ => -1,
            };
            if (pathBufferOffset < 0)
            {
                return LinkTargetProbe.Failed("unsupported-reparse-tag", returnedLength: returnedLength);
            }

            var pathFieldsOffset = ReparseHeaderSize;
            var substituteOffset = BitConverter.ToUInt16(buffer, pathFieldsOffset);
            var substituteLength = BitConverter.ToUInt16(buffer, pathFieldsOffset + 2);
            var printOffset = BitConverter.ToUInt16(buffer, pathFieldsOffset + 4);
            var printLength = BitConverter.ToUInt16(buffer, pathFieldsOffset + 6);
            var dataEnd = ReparseHeaderSize + dataLength;
            if (pathBufferOffset + substituteOffset + substituteLength > dataEnd
                || pathBufferOffset + printOffset + printLength > dataEnd)
            {
                return LinkTargetProbe.Failed("reparse-data-out-of-range", returnedLength: returnedLength);
            }

            var substitute = System.Text.Encoding.Unicode.GetString(
                buffer,
                pathBufferOffset + substituteOffset,
                substituteLength);
            var print = System.Text.Encoding.Unicode.GetString(
                buffer,
                pathBufferOffset + printOffset,
                printLength);
            var target = NormalizeReparseTarget(
                string.IsNullOrWhiteSpace(print) ? substitute : print);
            return target is null
                ? LinkTargetProbe.Failed("reparse-target-format", returnedLength: returnedLength)
                : LinkTargetProbe.Succeeded(target, returnedLength);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool OperatingSystemIsWindows() =>
        Environment.OSVersion.Platform == PlatformID.Win32NT;

    private static string ToExtendedWindowsPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? path
            : $@"\\?\{path}";

    private static string? NormalizeReparseTarget(string target)
    {
        if (target.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + target.Substring(8);
        }

        if (target.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return target.Substring(4);
        }

        if (target.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + target.Substring(8);
        }

        if (target.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return target.Substring(4);
        }

        return Path.IsPathRooted(target) ? target : null;
    }

    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint ReparseTagMountPoint = 0xA0000003;
    private const uint ReparseTagSymbolicLink = 0xA000000C;
    private const int MaximumReparseDataBufferSize = 16 * 1024;
    private const int ReparseHeaderSize = 8;
    private const int MountPointPathFieldsSize = 8;
    private const int SymbolicLinkPathFieldsSize = 12;
    private static readonly IntPtr InvalidHandle = new(-1);

    private sealed class LinkTargetProbe
    {
        private LinkTargetProbe(string? target, string failure, int win32Error, uint returnedLength)
        {
            Target = target;
            Failure = failure;
            Win32Error = win32Error;
            ReturnedLength = returnedLength;
        }

        public string? Target { get; }
        public string Failure { get; }
        public int Win32Error { get; }
        public uint ReturnedLength { get; }

        public static LinkTargetProbe Succeeded(string target, uint returnedLength) =>
            new(target, string.Empty, 0, returnedLength);

        public static LinkTargetProbe Failed(
            string failure,
            int win32Error = 0,
            uint returnedLength = 0) =>
            new(null, failure, win32Error, returnedLength);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        [System.Runtime.InteropServices.Out] byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
