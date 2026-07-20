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
    ExportResult[] ExportAllBlocks(string outputDir);

    /// <summary>Tag tables / UDTs export into Tags/ and UDT/ subfolders and upsert one metadata.json record per object (§13 step 2).</summary>
    ExportResult[] ExportTagTables(string outputDir, string? plcName);
    ExportResult[] ExportUdts(string outputDir, string? plcName);

    /// <summary>Incremental sync (buildnote/plan/export-sync.md): PLC software-checksum gate,
    /// timestamp-nominated diff, hash-confirmed re-export of changed components only.</summary>
    SyncResult[] SyncExport(string outputDir, string? plcName);
    ImportResult ImportBlock(string blockName, string xmlFilePath);

    CompileResult CompileBlock(string blockName);
    CompileResult CompilePlc();
}
