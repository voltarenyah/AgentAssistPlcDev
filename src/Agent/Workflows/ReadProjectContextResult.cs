using Contracts.Engineering;
using Contracts.Knowledge;

namespace Agent.Workflows;

/// <summary>Aggregate result of one confirmed context sync (buildnote/plan/export-sync.md §UI).</summary>
public sealed class ReadProjectContextResult
{
    public required string ProjectName { get; init; }
    public required string ExportRoot { get; init; }
    public required string DbPath { get; init; }

    /// <summary>Per-PLC sync outcomes (added/changed/touched/removed/failed per component).</summary>
    public required SyncResult[] Sync { get; init; }

    /// <summary>Ingest outcome, or null when the knowledge db was already fresh (skipped).</summary>
    public IngestResult? Ingest { get; init; }

    /// <summary>True when nothing changed and the knowledge db exists — the run was a no-op.</summary>
    public bool UpToDate { get; init; }
}
