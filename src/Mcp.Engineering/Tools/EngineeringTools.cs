using System.ComponentModel;
using Contracts;
using Contracts.Engineering;
using Contracts.Sandbox;
using Mcp.Engineering.Adapter;
using Mcp.Engineering.Openness;
using Mcp.Engineering.Sandbox;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Engineering.Tools;

/// <summary>
/// MCP tool surface (mcp-engineering.md §3). Thin wrappers over <see cref="IEngineeringPlatform"/>
/// (registered as <see cref="StaAdapter"/> so all Openness calls run on one STA thread);
/// all failures become structured isError results via <see cref="ToolJson"/>.
/// Every call passes the <see cref="EngineeringGuard"/> first: tier classification (default-deny
/// unknown) and the path jail run server-side, before any adapter code executes.
/// </summary>
[McpServerToolType]
public sealed class EngineeringTools
{
    private readonly IEngineeringPlatform _adapter;
    private readonly EngineeringGuard _guard;

    public EngineeringTools(IEngineeringPlatform adapter, EngineeringGuard guard)
    {
        _adapter = adapter;
        _guard = guard;
    }

    [McpServerTool(Name = "check_environment")]
    [Description("Validate TIA Openness installation, DLL paths, and user group membership (read-only).")]
    public CallToolResult CheckEnvironment() => Invoke("check_environment", () => _adapter.CheckEnvironment());

    [McpServerTool(Name = "list_sessions")]
    [Description("Enumerate running TIA Portal processes that can be attached to (read-only).")]
    public CallToolResult ListSessions() => Invoke("list_sessions", () => _adapter.ListSessions());

    [McpServerTool(Name = "get_current_session")]
    [Description("Report the TIA Portal session this server is currently attached to, if any (read-only).")]
    public CallToolResult GetCurrentSession() => Invoke("get_current_session", () => _adapter.GetCurrentSession());

    [McpServerTool(Name = "connect")]
    [Description("Attach to a running TIA session (sessionId) or open a project (projectPath; headless unless withUI=true). Provide exactly one.")]
    public CallToolResult Connect(
        [Description("TIA process id from list_sessions → attach mode.")] int? sessionId = null,
        [Description("Path to the .ap17 project file → open mode.")] string? projectPath = null,
        [Description("Open mode only: show the TIA UI. Default false (headless).")] bool withUI = false,
        [Description("Open an older project with TIA's upgrade workflow. Default false.")] bool upgrade = false,
        [Description("Project open mode: primary or secondary. Default primary.")] string openMode = "primary",
        [Description("Protected-project authentication: desktop_sso, anonymous, interactive, or credentials.")] string? authenticationMode = null,
        [Description("Max seconds to wait for Openness startup. Default 60.")] int timeoutSeconds = 60)
        => Invoke("connect", () => _adapter.Connect(new ConnectOptions
        {
            SessionId = sessionId,
            ProjectPath = projectPath,
            WithUI = withUI,
            Upgrade = upgrade,
            OpenMode = openMode,
            AuthenticationMode = authenticationMode,
            TimeoutSeconds = timeoutSeconds,
        }), ("projectPath", projectPath));

    [McpServerTool(Name = "disconnect")]
    [Description("Release project and portal handles without closing the project or the TIA instance, so it can be re-attached later. Never saves; reports unsaved changes.")]
    public CallToolResult Disconnect() => Invoke("disconnect", () => _adapter.Disconnect());

    [McpServerTool(Name = "close_session")]
    [Description("Close a TIA Portal session by process ID (sends close signal — same as clicking X). For sessions with UI, the user can save or discard changes.")]
    public CallToolResult CloseSession(
        [Description("TIA process ID from list_sessions.")] int sessionId)
        => Invoke("close_session", () => { _adapter.CloseSession(sessionId); return true; });

    [McpServerTool(Name = "save_project")]
    [Description("Explicitly save the open TIA project in place (save_project_as saves a copy to a new directory).")]
    public CallToolResult SaveProject() => Invoke("save_project", () => { _adapter.SaveProject(); return new { }; });

    [McpServerTool(Name = "create_project")]
    [Description("Create a new TIA project through the connected Openness portal and return the actual project path.")]
    public CallToolResult CreateProject(
        [Description("Directory that will contain the new project folder.")] string targetDirectory,
        [Description("Project name.")] string projectName)
        => Invoke(
            "create_project",
            () => _adapter.CreateProject(new DirectoryInfo(targetDirectory), projectName),
            ("targetDirectory", targetDirectory));

    [McpServerTool(Name = "archive_project")]
    [Description("Archive the currently open TIA project to a file in the target directory.")]
    public CallToolResult ArchiveProject(
        [Description("Directory where the archive file will be written.")] string targetDirectory,
        [Description("Archive file name, without a directory path.")] string archiveName,
        [Description("Archive mode: compressed, none, discard_restorable_data, or discard_restorable_data_and_compressed.")] string archivationMode = "compressed")
        => Invoke(
            "archive_project",
            () => _adapter.ArchiveProject(new DirectoryInfo(targetDirectory), archiveName, archivationMode),
            ("targetDirectory", targetDirectory));

    [McpServerTool(Name = "retrieve_project")]
    [Description("Retrieve a TIA project archive into a target directory and report the actual project path. Use openMode=secondary to avoid replacing the primary project in the connected portal.")]
    public CallToolResult RetrieveProject(
        [Description("Path to the TIA project archive file.")] string archivePath,
        [Description("Directory where the project folder will be retrieved.")] string targetDirectory,
        [Description("Upgrade the retrieved project while opening it. Default false.")] bool upgrade = false,
        [Description("Open mode: primary or secondary. Default primary.")] string openMode = "primary")
        => Invoke(
            "retrieve_project",
            () => _adapter.RetrieveProject(new FileInfo(archivePath), new DirectoryInfo(targetDirectory), upgrade, openMode),
            ("archivePath", archivePath),
            ("targetDirectory", targetDirectory));

    [McpServerTool(Name = "save_project_as")]
    [Description("Save the open TIA project to a new directory (TIA Save As) and switch the session to the managed copy. Returns the actual managed project path reported by TIA — never a constructed path.")]
    public CallToolResult SaveProjectAs(
        [Description("Target directory for the managed project copy.")] string targetDirectory)
        => Invoke(
            "save_project_as",
            () => new { managedProjectPath = _adapter.SaveProjectAs(new DirectoryInfo(targetDirectory)) },
            ("targetDirectory", targetDirectory));

    [McpServerTool(Name = "get_project_info")]
    [Description("Project name, path, PLC devices, block count, last modified (read-only).")]
    public CallToolResult GetProjectInfo() => Invoke("get_project_info", () => _adapter.GetProjectInfo());

    [McpServerTool(Name = "get_project_capabilities")]
    [Description("Read project access state, primary/secondary status, unsaved state, and protected-project authentication modes.")]
    public CallToolResult GetProjectCapabilities() =>
        Invoke("get_project_capabilities", () => _adapter.GetProjectCapabilities());

    [McpServerTool(Name = "list_blocks")]
    [Description("Enumerate all blocks (OB/FB/FC/DB) incl. nested block groups (read-only).")]
    public CallToolResult ListBlocks(
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null)
        => Invoke("list_blocks", () => _adapter.ListBlocks(plcName));

    [McpServerTool(Name = "get_plc_checksums")]
    [Description("Read the current compiled software checksum for one or all PLC devices. No exports or writes.")]
    public CallToolResult GetPlcChecksums(
        [Description("PLC device name; omit to read every PLC device.")] string? plcName = null)
        => Invoke("get_plc_checksums", () => _adapter.GetPlcChecksums(plcName));

    [McpServerTool(Name = "export_block")]
    [Description("Export a single block to XML under outputDir/Blocks|DB and upsert its record in outputDir/metadata.json (read-only w.r.t. the project).")]
    public CallToolResult ExportBlock(
        [Description("Block name as listed by list_blocks.")] string blockName,
        [Description("Export root directory.")] string outputDir)
        => Invoke("export_block", () => _adapter.ExportBlock(blockName, outputDir), ("outputDir", outputDir));

    [McpServerTool(Name = "export_source_object")]
    [Description("Export exactly one block (OB/FB/FC/DB), tag table (Tags), or UDT to XML under outputDir and upsert its record in outputDir/metadata.json (read-only w.r.t. the project).")]
    public CallToolResult ExportSourceObject(
        [Description("Object name as listed by list_blocks / compare_context.")] string name,
        [Description("Category: OB, FB, FC, DB, Tags, or UDT.")] string category,
        [Description("Export root directory.")] string outputDir,
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null)
        => Invoke("export_source_object", () => _adapter.ExportSourceObject(name, category, outputDir, plcName), ("outputDir", outputDir));

    [McpServerTool(Name = "export_all_blocks")]
    [Description("Export every PLC block to XML under outputDir (Blocks/ and DB/ subfolders; per-PLC subfolder when multiple PLCs) and write a metadata.json manifest per export root.")]
    public CallToolResult ExportAllBlocks(
        [Description("Export root directory for the XML files.")] string outputDir,
        IProgress<ProgressNotificationValue>? progress = null)
        => Invoke("export_all_blocks", () => _adapter.ExportAllBlocks(outputDir, ToEngineeringProgress(progress)), ("outputDir", outputDir));

    [McpServerTool(Name = "export_tag_tables")]
    [Description("Export every PLC tag table to XML under outputDir/Tags (recursing nested groups) and upsert one metadata.json record per table. Per-PLC subfolder when the project has multiple PLCs, unless plcName is given.")]
    public CallToolResult ExportTagTables(
        [Description("Export root directory.")] string outputDir,
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null,
        IProgress<ProgressNotificationValue>? progress = null)
        => Invoke("export_tag_tables", () => _adapter.ExportTagTables(outputDir, plcName, ToEngineeringProgress(progress)), ("outputDir", outputDir));

    [McpServerTool(Name = "export_udts")]
    [Description("Export every PLC data type (UDT) to XML under outputDir/UDT (recursing nested groups) and upsert one metadata.json record per type. Per-PLC subfolder when the project has multiple PLCs, unless plcName is given.")]
    public CallToolResult ExportUdts(
        [Description("Export root directory.")] string outputDir,
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null,
        IProgress<ProgressNotificationValue>? progress = null)
        => Invoke("export_udts", () => _adapter.ExportUdts(outputDir, plcName, ToEngineeringProgress(progress)), ("outputDir", outputDir));

    [McpServerTool(Name = "sync_export")]
    [Description("Incrementally sync an export root with the current project state: PLC software-checksum gate (skip everything when unchanged), then a timestamp-nominated, hash-confirmed diff that re-exports only real changes and drops components deleted in TIA. Read-only w.r.t. the project. Run ingest_source afterwards to refresh the knowledge base.")]
    public CallToolResult SyncExport(
        [Description("Export root directory previously filled by export_all_blocks / export_tag_tables / export_udts.")] string outputDir,
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null,
        IProgress<ProgressNotificationValue>? progress = null)
        => Invoke("sync_export", () => _adapter.SyncExport(outputDir, plcName, ToEngineeringProgress(progress)), ("outputDir", outputDir));

    [McpServerTool(Name = "rebuild_export")]
    [Description("Complete full export. With plcName, exports that selected device directly into outputDir; otherwise exports all devices into per-device subfolders and writes project metadata. Always rewrites manifests and never performs an incremental diff. Read-only w.r.t. the project.")]
    public CallToolResult RebuildExport(
        [Description("Export root directory. With plcName, this is the selected device's direct staging root; otherwise it contains per-device subfolders.")] string outputDir,
        [Description("PLC device name for a direct full device export; omit to rebuild every PLC into per-device subfolders.")] string? plcName = null,
        IProgress<ProgressNotificationValue>? progress = null)
        => Invoke("rebuild_export", () => _adapter.RebuildExport(outputDir, plcName, ToEngineeringProgress(progress)), ("outputDir", outputDir));

    [McpServerTool(Name = "export_hardware_configuration")]
    [Description("Export the TIA V17 CAx hardware configuration as a canonical project AML and, optionally, one AML per device under Devices. Read-only with respect to the project.")]
    public CallToolResult ExportHardwareConfiguration(
        [Description("Export root directory. The project AML is written directly under outputDir/project.aml.")] string outputDir,
        [Description("Also export one AML per TIA device under outputDir/Devices. Default false — project-level AML only; per-device CAx is slow on big projects.")] bool includeDeviceExports = false,
        IProgress<ProgressNotificationValue>? progress = null)
        => Invoke(
            "export_hardware_configuration",
            () => _adapter.ExportHardwareConfiguration(outputDir, includeDeviceExports, ToEngineeringProgress(progress)),
            ("outputDir", outputDir));

    [McpServerTool(Name = "get_context_status")]
    [Description("Check whether an export root matches the current project state without changing anything: per PLC, the stored manifest checksum vs the live software checksum (states: no-baseline / in-sync / changed / unknown). No exports, no writes — safe to run anytime.")]
    public CallToolResult GetContextStatus(
        [Description("Export root directory to check.")] string outputDir,
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null)
        => Invoke("get_context_status", () => _adapter.GetContextStatus(outputDir, plcName), ("outputDir", outputDir));

    [McpServerTool(Name = "compare_context")]
    [Description("Per-component read-only diff between the live project and an export root's manifest: for every block/tag table/UDT, per-fingerprint component matches with stored/live hashes, modified dates, and a verdict (same / different / new / missing / unknown). No exports, no writes — the data behind a compare view before deciding to sync.")]
    public CallToolResult CompareContext(
        [Description("Export root directory to compare against.")] string outputDir,
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null)
        => Invoke("compare_context", () => _adapter.CompareContext(outputDir, plcName), ("outputDir", outputDir));

    [McpServerTool(Name = "import_block")]
    [Description("Import a modified block XML back into the project (DESTRUCTIVE: overwrites the block). Caller must validate the XML and snapshot the working folder first.")]
    public CallToolResult ImportBlock(
        [Description("Block name to overwrite.")] string blockName,
        [Description("Path to the modified XML file.")] string xmlFilePath,
        [Description("PLC device name; optional for single-PLC projects and required when the project contains multiple PLCs.")] string? plcName = null)
        => Invoke("import_block", () => _adapter.ImportBlock(blockName, xmlFilePath, plcName), ("xmlFilePath", xmlFilePath));

    [McpServerTool(Name = "import_hardware_configuration")]
    [Description("Import a TIA Openness CAx hardware configuration from an AML file (DESTRUCTIVE). CAx import is performed through the project CaxProvider and uses the selected conflict policy.")]
    public CallToolResult ImportHardwareConfiguration(
        [Description("Path to the CAx AML file.")] string amlFilePath,
        [Description("Optional path for the TIA CAx import log. A temporary path is used when omitted.")] string? logFilePath = null,
        [Description("Conflict policy: move_to_parking_lot, retain_tia_device, or overwrite_tia_device.")] string conflictPolicy = "move_to_parking_lot")
        => Invoke(
            "import_hardware_configuration",
            () => _adapter.ImportHardwareConfiguration(amlFilePath, logFilePath, ParseHardwareImportPolicy(conflictPolicy)),
            ("amlFilePath", amlFilePath),
            ("logFilePath", logFilePath));

    [McpServerTool(Name = "create_block")]
    [Description("Create a native TIA V17 PLC block. Supported block types are FB and InstanceDB; use import_block for XML-defined FC, OB, or DB blocks.")]
    public CallToolResult CreateBlock(
        [Description("Name of the new block.")] string blockName,
        [Description("Native block type: FB or InstanceDB.")] string blockType,
        [Description("Block number. Use 0 for automatic numbering.")] int number = 0,
        [Description("Programming language for FB, for example LAD, FBD, or SCL.")] string? programmingLanguage = null,
        [Description("Existing FB name when creating an InstanceDB.")] string? instanceOfName = null,
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null)
        => Invoke("create_block", () => _adapter.CreateBlock(blockName, blockType, number, programmingLanguage, instanceOfName, plcName));

    [McpServerTool(Name = "delete_block")]
    [Description("Delete a PLC block from the selected TIA project (DESTRUCTIVE). The block must be closed in the TIA editor.")]
    public CallToolResult DeleteBlock(
        [Description("Name of the block to delete.")] string blockName,
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null)
        => Invoke("delete_block", () => _adapter.DeleteBlock(blockName, plcName));

    [McpServerTool(Name = "import_source_object")]
    [Description("Import an existing block, tag table, or UDT XML source into its exact TIA group (DESTRUCTIVE: overwrites the existing object; add/delete/rename is unsupported). Caller must validate the XML and snapshot the working folder first.")]
    public CallToolResult ImportSourceObject(
        [Description("Managed PLC source path, for example Blocks/Area/Main [OB1].xml, Tags/LineA/Inputs.xml, or UDT/Models/Motor.xml.")] string relativePath,
        [Description("Path to the modified XML file.")] string xmlFilePath,
        [Description("PLC device name; optional for single-PLC projects and required when the project contains multiple PLCs.")] string? plcName = null)
    {
        SandboxTier tier;
        try
        {
            tier = _guard.CheckSourceObjectImport(relativePath, xmlFilePath);
        }
        catch (SandboxException se)
        {
            return ToolJson.Fail(se.Code, se.Message, se.Remediation);
        }

        try
        {
            var result = ToolJson.Ok(_adapter.ImportSourceObject(relativePath, xmlFilePath, plcName));
            _guard.AuditAllow("import_source_object", tier, $"relativePath={relativePath}; xmlFilePath={xmlFilePath}");
            return result;
        }
        catch (AdapterException ae)
        {
            return ToolJson.Fail(ae.Code, ae.Message, ae.Remediation);
        }
        catch (Exception ex)
        {
            var mapped = OpennessErrorMapper.Map(ex);
            return ToolJson.Fail(mapped.Code, ex.Message, mapped.Remediation);
        }
    }

    [McpServerTool(Name = "compile_block")]
    [Description("Compile the PLC software and report messages for the named block (write: mutates project compile state). V17 has no per-block compile — this is compile_plc + per-block filtering.")]
    public CallToolResult CompileBlock(
        [Description("Block name to report on.")] string blockName,
        [Description("PLC device name; optional for single-PLC projects and required when the project contains multiple PLCs.")] string? plcName = null)
        => Invoke("compile_block", () => _adapter.CompileBlock(blockName, plcName));

    [McpServerTool(Name = "compile_plc")]
    [Description("Compile the whole PLC software, returning all messages (write: mutates project compile state).")]
    public CallToolResult CompilePlc(
        [Description("PLC device name; optional for single-PLC projects and required when the project contains multiple PLCs.")] string? plcName = null)
        => Invoke("compile_plc", () => _adapter.CompilePlc(plcName));

    [McpServerTool(Name = "open_block_in_editor")]
    [Description("Open a block in the TIA Portal editor window. Requires a UI-connected TIA session.")]
    public CallToolResult OpenBlockInEditor(
        [Description("Block name to open.")] string blockName)
        => Invoke("open_block_in_editor", () => { _adapter.OpenBlockInEditor(blockName); return true; });

    [McpServerTool(Name = "open_source_object_in_editor")]
    [Description("Open a block (OB/FB/FC/DB), tag table (Tags), or UDT in the TIA Portal editor window. Requires a UI-connected TIA session.")]
    public CallToolResult OpenSourceObjectInEditor(
        [Description("Object name to open.")] string name,
        [Description("Category: OB, FB, FC, DB, Tags, or UDT.")] string category,
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null)
        => Invoke("open_source_object_in_editor", () => _adapter.OpenSourceObjectInEditor(name, category, plcName));

    private CallToolResult Invoke(string tool, Func<object> action, params (string Name, string? Value)[] pathArguments)
    {
        SandboxTier tier;
        try
        {
            tier = _guard.Check(tool, pathArguments);
        }
        catch (SandboxException se)
        {
            return ToolJson.Fail(se.Code, se.Message, se.Remediation);
        }

        try
        {
            var result = ToolJson.Ok(action());
            _guard.AuditAllow(tool, tier, Summarize(pathArguments));
            return result;
        }
        catch (AdapterException ae)
        {
            return ToolJson.Fail(ae.Code, ae.Message, ae.Remediation);
        }
        catch (Exception ex)
        {
            var mapped = OpennessErrorMapper.Map(ex);
            return ToolJson.Fail(mapped.Code, ex.Message, mapped.Remediation);
        }
    }

    private static string? Summarize((string Name, string? Value)[] pathArguments)
    {
        var paths = pathArguments.Where(argument => argument.Value != null).ToArray();
        return paths.Length == 0
            ? null
            : string.Join("; ", paths.Select(argument => $"{argument.Name}={argument.Value}"));
    }

    private static HardwareImportConflictPolicy ParseHardwareImportPolicy(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "move_to_parking_lot" or "movetoparkinglot" => HardwareImportConflictPolicy.MoveToParkingLot,
            "retain_tia_device" or "retaintia" or "retaintia device" => HardwareImportConflictPolicy.RetainTiaDevice,
            "overwrite_tia_device" or "overwritetia" or "overwrite" => HardwareImportConflictPolicy.OverwriteTiaDevice,
            _ => throw new AdapterException(
                "INVALID_HARDWARE_IMPORT_POLICY",
                $"Unknown hardware import conflict policy '{value}'.",
                "Use move_to_parking_lot, retain_tia_device, or overwrite_tia_device."),
        };

    private static IProgress<EngineeringProgress>? ToEngineeringProgress(
        IProgress<ProgressNotificationValue>? progress) =>
        progress is null ? null : new McpProgressBridge(progress);

    private sealed class McpProgressBridge(IProgress<ProgressNotificationValue> progress) : IProgress<EngineeringProgress>
    {
        public void Report(EngineeringProgress value)
        {
            if (!string.IsNullOrWhiteSpace(value.Message))
            {
                progress.Report(new ProgressNotificationValue
                {
                    Progress = 0,
                    Message = value.Message,
                });
            }
        }
    }
}
