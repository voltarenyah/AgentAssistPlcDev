namespace Contracts.Engineering;

/// <summary>
/// XML comparison helpers for export round-trip verification (mcp-engineering.md §6.1 item 5).
/// Shared with the source-editor MCP (net8) — Spike B proved comment-only edits round-trip
/// byte-stable except the &lt;Created&gt; export timestamp.
/// </summary>
public static class XmlCompare
{
    /// <summary>Normalizes line endings and strips &lt;Created&gt; timestamp lines.</summary>
    public static string Normalize(string xml) =>
        string.Join("\n", xml.Replace("\r", "").Split('\n')
            .Where(line => !line.TrimStart().StartsWith("<Created>")));
}
