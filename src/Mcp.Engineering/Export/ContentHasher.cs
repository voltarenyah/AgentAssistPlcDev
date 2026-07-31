using Contracts.Engineering;

namespace Mcp.Engineering.Export;

/// <summary>
/// Content hash of an exported XML file — delegates to <see cref="XmlContentHash"/> (SHA256 over
/// the normalized text, base64url). Kept as the manifest-side name for the tier-2 confirmation in
/// sync_export: timestamps nominate, the hash decides (buildnote/plan/export-sync.md). The
/// workbench reconciler shares the same helper so both sides of a refresh agree on "changed".
/// </summary>
internal static class ContentHasher
{
    /// <summary>Hash of the file's normalized content; null when the file cannot be read.</summary>
    public static string? TryCompute(string path) => XmlContentHash.TryComputeFile(path);

    /// <summary>Hash of raw XML text after normalization. Internal for tests.</summary>
    internal static string Compute(string xml) => XmlContentHash.Compute(xml);
}
