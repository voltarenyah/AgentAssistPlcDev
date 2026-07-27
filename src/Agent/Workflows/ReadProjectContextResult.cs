using Contracts.Engineering;
using Contracts.Knowledge;

namespace Agent.Workflows;

/// <summary>Aggregate result of one confirmed context sync (buildnote/plan/export-sync.md §UI).</summary>
public sealed class ReadProjectContextResult
{
    public required string ProjectName { get; init; }
    public required string WorkbenchId { get; init; }
    public required string WorktreeId { get; init; }
    public required string DeviceId { get; init; }
    public required string PlcName { get; init; }
    public required string ExportRoot { get; init; }
    public required string ModifiedSourceRoot { get; init; }
    public required string StagingRoot { get; init; }
    public required string DbPath { get; init; }

    /// <summary>Per-PLC sync outcomes (added/changed/touched/removed/failed per component).</summary>
    public required SyncResult[] Sync { get; init; }

    /// <summary>
    /// Legacy compatibility field. Device-context staging never mutates knowledge, so this is null.
    /// Use <see cref="Agent.Workbench.WorkbenchCoordinator"/> explicitly after approval.
    /// </summary>
    public IngestResult? Ingest { get; init; }

    /// <summary>True when the staged export contains differences requiring user approval.</summary>
    public bool ApprovalRequired { get; init; }

    /// <summary>True when nothing changed and the knowledge db exists — the run was a no-op.</summary>
    public bool UpToDate { get; init; }
}
