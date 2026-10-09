namespace Mcp.Engineering.Export;

/// <summary>
/// The pure part of a path-scoped source read: which user-group path and name a manifest source path
/// names, and whether an object found there is the one the request asked for. Deliberately free of
/// Siemens types, because the rule that a mismatch must fall back to the complete walk is the part
/// that has to be provable without TIA — a scoped read may cost time, never correctness.
/// </summary>
internal static class ScopedSourceRequest
{
    /// <summary>
    /// Splits a manifest source path into its '/'-joined user-group path (null at the group root) and
    /// the object name. False when there is no path to descend, which the caller answers with the
    /// complete walk.
    /// </summary>
    public static bool TrySplit(string? sourcePath, out string? groupPath, out string name)
    {
        groupPath = null;
        name = string.Empty;
        if (string.IsNullOrEmpty(sourcePath))
            return false;

        var separator = sourcePath.LastIndexOf('/');
        // A leading separator names no group and a trailing one names no object; neither is a source
        // path a manifest can hold, so the caller takes the complete walk instead of guessing.
        if (separator == 0 || separator == sourcePath.Length - 1)
            return false;

        if (separator < 0)
        {
            name = sourcePath;
            return true;
        }

        groupPath = sourcePath[..separator];
        name = sourcePath[(separator + 1)..];
        return true;
    }

    /// <summary>
    /// True when the object found at <paramref name="sourcePath"/> with <paramref name="category"/>
    /// re-derives exactly the requested id — the same identity the manifest recorded for it.
    /// </summary>
    public static bool MatchesRequestedId(string requestedId, string category, string sourcePath) =>
        string.Equals(StableId.Create(category, sourcePath), requestedId, StringComparison.Ordinal);
}
