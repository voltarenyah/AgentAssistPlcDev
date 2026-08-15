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

    /// <summary>Report the currently attached TIA Portal session, if any (read-only).</summary>
    CurrentSessionInfo GetCurrentSession();

    ConnectionInfo Connect(ConnectOptions options);
    DisconnectResult Disconnect();

    /// <summary>Explicit save — together with <see cref="SaveProjectAs"/> the only operations that
    /// persist the project (locked decision §1.1, amended by the vc-restructure plan to add SaveAs).</summary>
    void SaveProject();

    ProjectCreateResult CreateProject(DirectoryInfo targetDirectory, string projectName);
    ProjectArchiveResult ArchiveProject(
        DirectoryInfo targetDirectory,
        string archiveName,
        string archivationMode = "compressed");
    ProjectRetrieveResult RetrieveProject(
        FileInfo archivePath,
        DirectoryInfo targetDirectory,
        bool upgrade = false,
        string openMode = "primary");

    /// <summary>Save the open project to a new directory (TIA Openness
    /// <c>Project.SaveAs(DirectoryInfo)</c> contract) and switch the session to the managed copy.
    /// Returns the ACTUAL managed project path reported by the adapter after SaveAs — callers must
    /// never construct the .ap17 path by assumption.</summary>
    string SaveProjectAs(DirectoryInfo targetDirectory);

    ProjectInfo GetProjectInfo();
    ProjectCapabilities GetProjectCapabilities();
    BlockInfo[] ListBlocks(string? plcName);
    PlcChecksumInfo[] GetPlcChecksums(string? plcName = null);

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

    /// <summary>Export the canonical project-level CAx AML and, optionally, one derived AML per TIA device.</summary>
    HardwareExportResult[] ExportHardwareConfiguration(
        string outputDir,
        bool includeDeviceExports = true,
        IProgress<EngineeringProgress>? progress = null);

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

    /// <summary>Import a CAx/AML hardware configuration through the TIA Openness CaxProvider.</summary>
    HardwareImportResult ImportHardwareConfiguration(
        string amlFilePath,
        string? logFilePath = null,
        HardwareImportConflictPolicy conflictPolicy = HardwareImportConflictPolicy.MoveToParkingLot);

    /// <summary>Create a native V17 block. Supported types are FB and InstanceDB.</summary>
    BlockInfo CreateBlock(
        string blockName,
        string blockType,
        int number = 0,
        string? programmingLanguage = null,
        string? instanceOfName = null,
        string? plcName = null);

    /// <summary>Delete a block from the selected PLC.</summary>
    BlockMutationResult DeleteBlock(string blockName, string? plcName = null);

    /// <summary>Overwrite one existing block, tag table, or UDT from its managed XML source path.</summary>
    SourceObjectImportResult ImportSourceObject(string relativePath, string xmlFilePath, string? plcName = null);

    CompileResult CompileBlock(string blockName, string? plcName = null);
    CompileResult CompilePlc(string? plcName = null);

    /// <summary>Open a block in the TIA Portal editor window. Requires a UI-connected session.</summary>
    void OpenBlockInEditor(string blockName);
}
