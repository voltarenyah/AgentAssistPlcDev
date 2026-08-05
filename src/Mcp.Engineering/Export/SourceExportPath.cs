namespace Mcp.Engineering.Export;

/// <summary>Builds stable managed-source paths from an object category and TIA group path.</summary>
public static class SourceExportPath
{
    public static string Build(string category, string? groupPath, string fileName)
    {
        ValidateSegment(category, nameof(category));
        ValidateFileName(fileName);

        var parts = new List<string> { category };
        if (!string.IsNullOrWhiteSpace(groupPath))
        {
            var rawGroupPath = groupPath!;
            if (Path.IsPathRooted(rawGroupPath) || rawGroupPath.StartsWith("/", StringComparison.Ordinal))
                throw new ArgumentException("The group path must be relative.", nameof(groupPath));

            var segments = rawGroupPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
                ValidateSegment(segment, nameof(groupPath));
            parts.AddRange(segments);
        }

        parts.Add(fileName);
        return string.Join("/", parts);
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName)
            || fileName.Contains('/') || fileName.Contains('\\')
            || fileName is "." or "..")
            throw new ArgumentException("The source filename must be a single relative file name.", nameof(fileName));
    }

    private static void ValidateSegment(string segment, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".."
            || segment.Contains('/') || segment.Contains('\\') || Path.IsPathRooted(segment))
            throw new ArgumentException("Source path segments must be relative and non-traversing.", parameterName);
    }
}
