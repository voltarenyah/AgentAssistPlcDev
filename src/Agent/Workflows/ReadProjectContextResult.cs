using Contracts.Knowledge;

namespace App.Workflows;

/// <summary>Aggregate result of one Read Project Context run (buildnote/plan/app.md §4).</summary>
public sealed class ReadProjectContextResult
{
    public required string ProjectName { get; init; }
    public required string ExportRoot { get; init; }
    public required string DbPath { get; init; }
    public int BlocksExported { get; init; }
    public int TagTablesExported { get; init; }
    public int UdtsExported { get; init; }
    public required IngestResult Ingest { get; init; }
}
