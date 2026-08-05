namespace Mcp.VersionControl.Git;

/// <summary>
/// Defines the repository-relative PLC source paths that version-control read and
/// write surfaces may expose. Git paths are returned with forward slashes.
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
        var segments = normalized.Split('/');
        if (segments.Length < 4 ||
            segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.Contains(':', StringComparison.Ordinal)) ||
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
