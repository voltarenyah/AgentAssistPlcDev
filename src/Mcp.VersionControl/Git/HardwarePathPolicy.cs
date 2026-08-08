namespace Mcp.VersionControl.Git;

/// <summary>
/// Defines the repository-relative hardware configuration paths that the app-internal
/// hardware commit surface may stage. Git paths are returned with forward slashes.
/// The transient hardware/staging export area is never committable.
/// </summary>
internal static class HardwarePathPolicy
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
        if (segments.Length < 2 ||
            segments.Any(segment =>
                segment.Length == 0 ||
                segment is "." or ".." ||
                segment.Contains(':', StringComparison.Ordinal)) ||
            !segments[0].Equals("hardware", StringComparison.OrdinalIgnoreCase) ||
            (segments.Length > 1 && segments[1].Equals("staging", StringComparison.OrdinalIgnoreCase)))
        {
            throw CreateError(path);
        }

        return normalized;
    }

    private static VcInternalException CreateError(string? path) => new(
        "HARDWARE_PATH_REQUIRED",
        $"'{path}' is not a committable hardware configuration path.");
}
