using Contracts.Engineering;

namespace Contracts;

/// <summary>
/// Version-agnostic contract between an engineering-software adapter (TIA V17 today;
/// Rockwell L5X, TIA V18+ later) and the MCP server hosting it.
/// Tool surface: buildnote/plan/mcp-engineering.md §3 (Phase 1 = 12 tools).
/// </summary>
public interface IEngineeringPlatform : IDisposable
{
    EnvCheckResult CheckEnvironment();
    SessionInfo[] ListSessions();
    ConnectionInfo Connect(ConnectOptions options);
    DisconnectResult Disconnect();

    /// <summary>Explicit save — the ONLY operation that persists the project (locked decision §1.1).</summary>
    void SaveProject();

    ProjectInfo GetProjectInfo();
    BlockInfo[] ListBlocks(string? plcName);

    ExportResult ExportBlock(string blockName, string outputDir);
    ExportResult[] ExportAllBlocks(string outputDir, IProgress<EngineeringProgress>? progress = null);

    /// <summary>Tag tables / UDTs export into Tags/ and UDT/ subfolders and upsert one metadata.json record per object (§13 step 2).</summary>
    ExportResult[] ExportTagTables(string outputDir, string? plcName, IProgress<EngineeringProgress>? progress = null);
    ExportResult[] ExportUdts(string outputDir, string? plcName, IProgress<EngineeringProgress>? progress = null);

    /// <summary>Incremental sync (buildnote/plan/export-sync.md): PLC software-checksum gate,
    /// timestamp-nominated diff, hash-confirmed re-export of changed components only.</summary>
    SyncResult[] SyncExport(string outputDir, string? plcName, IProgress<EngineeringProgress>? progress = null);

    /// <summary>Full rebuild export: scans all PLC devices in the project, exports every component
    /// (blocks, tag tables, UDTs) to fresh per-device subfolders under outputDir, and writes
    /// project-level metadata (sourceProjectPath, per-device checksums, device list). Always
    /// rewrites the device manifests — no incremental diff. Used when the device set changes or
    /// the export structure needs a clean rebuild.</summary>
    SyncResult[] RebuildExport(string outputDir, string? plcName = null, IProgress<EngineeringProgress>? progress = null);

    /// <summary>Close a TIA Portal session by process ID. Sends a close signal to the portal
    /// window (same as clicking the X button). The user can save or discard any project changes.</summary>
    void CloseSession(int sessionId);

    /// <summary>Read-only counterpart of <see cref="SyncExport"/> (buildnote/plan/export-sync.md §UI):
    /// stored vs live checksum per PLC export root — no exports, no writes.</summary>
    ContextStatusResult[] GetContextStatus(string outputDir, string? plcName);

    /// <summary>Read-only per-component diff (buildnote/plan/export-sync.md §Compare): live
    /// fingerprints/timestamps vs the stored manifest — the Compare tab's data source.</summary>
    ContextCompareResult[] CompareContext(string outputDir, string? plcName);
    /// <summary>Import a block into the selected PLC. The PLC name may be omitted only when the
    /// project contains a single PLC.</summary>
    ImportResult ImportBlock(string blockName, string xmlFilePath, string? plcName = null);

    CompileResult CompileBlock(string blockName, string? plcName = null);
    CompileResult CompilePlc();

    /// <summary>Open a block in the TIA Portal editor window. Requires a UI-connected session.</summary>
    void OpenBlockInEditor(string blockName);
}
