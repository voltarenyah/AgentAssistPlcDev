namespace Contracts.Knowledge;

/// <summary>
/// ingest_source result (buildnote/plan/mcp-knowledge.md §6).
/// Promoted from Mcp.Knowledge's anonymous result for the App (buildnote/plan/app.md §2.2);
/// the serialized JSON shape is unchanged (camelCase: dbPath, source, filesFound, …).
/// </summary>
public sealed class IngestResult
{
    public string DbPath { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public int FilesFound { get; set; }
    public int FilesImported { get; set; }
    public int Nodes { get; set; }
    public int Edges { get; set; }
    public SortedDictionary<string, int> ByKind { get; set; } = new(StringComparer.Ordinal);
    public List<string> Warnings { get; set; } = new();
    public long DurationMs { get; set; }
}
