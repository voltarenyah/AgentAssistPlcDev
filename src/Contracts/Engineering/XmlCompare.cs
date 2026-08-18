using System.Text.RegularExpressions;

namespace Contracts.Engineering;

/// <summary>
/// XML comparison helpers for export round-trip verification (mcp-engineering.md §6.1 item 5).
/// Shared with the source-editor MCP (net8) — Spike B proved comment-only edits round-trip
/// byte-stable except export timestamps and generated CAx IDs.
/// </summary>
public static class XmlCompare
{
    private static readonly Regex GeneratedGuid = new(
        @"(?<![0-9a-f])[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![0-9a-f])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Normalizes line endings, strips export timestamps, and masks generated CAx IDs.</summary>
    public static string Normalize(string xml)
    {
        var withoutExportTimestamps = string.Join("\n", xml.Replace("\r", "").Split('\n')
            .Where(line =>
                !line.TrimStart().StartsWith("<Created>")
                && !line.TrimStart().StartsWith("<LastWritingDateTime>")));
        return GeneratedGuid.Replace(withoutExportTimestamps, "{GUID}");
    }
}
