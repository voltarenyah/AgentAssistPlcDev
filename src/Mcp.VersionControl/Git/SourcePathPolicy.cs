namespace Mcp.VersionControl.Git;

/// <summary>
/// Defines the repository-relative paths that version-control read and
/// write surfaces may expose: PLC source XML under devices/&lt;device&gt;/source/**/*.xml,
/// hardware configuration exports under hardware/** (except the ignored hardware/staging/
/// scratch area), and the single engineering-state/revision.json metadata file.
/// Git paths are returned with forward slashes.
/// </summary>
internal static class SourcePathPolicy
{
    public static string Require(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathRooted(path) ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("\\", StringComparison.Ordinal))
        {
            throw CreateError(path);
        }

        var normalized = path.Replace('\\', '/');

        // Single git-tracked metadata file linking git commits to SVN native revisions.
        if (normalized.Equals("engineering-state/revision.json", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var segments = normalized.Split('/');
        if (segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.Contains(':', StringComparison.Ordinal)))
        {
            throw CreateError(path);
        }

        // Tracked hardware configuration exports (project AML); the staging scratch area
        // stays invisible.
        if (segments.Length >= 2
            && segments[0].Equals("hardware", StringComparison.OrdinalIgnoreCase)
            && !segments[1].Equals("staging", StringComparison.OrdinalIgnoreCase)
            && segments[^1].Contains('.', StringComparison.Ordinal))
        {
            return normalized;
        }

        if (segments.Length < 4 ||
            !segments[0].Equals("devices", StringComparison.OrdinalIgnoreCase) ||
            !segments[2].Equals("source", StringComparison.OrdinalIgnoreCase) ||
            segments[^1].Length <= ".xml".Length ||
            !segments[^1].EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            throw CreateError(path);
        }

        return normalized;
    }

    public static bool IsAllowed(string path)
    {
        try
        {
            _ = Require(path);
            return true;
        }
        catch (VcInternalException)
        {
            return false;
        }
    }

    private static VcInternalException CreateError(string? path) => new(
        "SOURCE_PATH_REQUIRED",
        $"'{path}' is not a tracked PLC source XML path.");
}
