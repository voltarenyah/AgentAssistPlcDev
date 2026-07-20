namespace Contracts.Sandbox;

/// <summary>
/// Filesystem jail for path arguments (outputDir, xmlFilePath, projectPath, …): canonicalizes
/// the path (collapsing "..") and rejects anything outside the configured roots or on the
/// network (UNC). Roots are compared case-insensitively (Windows).
/// </summary>
public sealed class PathJail
{
    private readonly List<string> roots;

    public PathJail(IEnumerable<string> roots)
    {
        this.roots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(CanonicalRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> Roots => roots;

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

        foreach (var root in roots)
        {
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }
        }

        throw new SandboxException(
            "SANDBOX_PATH_DENIED",
            $"{parameterName}: '{full}' is outside the sandbox roots ({string.Join("; ", roots)}).",
            $"Add the directory to allowedRoots in {SandboxConfig.DefaultFilePath}.");
    }

    /// <summary>Canonical root with a trailing separator, so "C:\roots" cannot match "C:\roots-eve".</summary>
    private static string CanonicalRoot(string root)
    {
        var full = Path.GetFullPath(root.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full + Path.DirectorySeparatorChar;
    }
}
