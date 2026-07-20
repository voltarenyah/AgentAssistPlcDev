using System.ComponentModel;
using Contracts;
using Contracts.Engineering;
using Contracts.Sandbox;
using Mcp.Engineering.Adapter;
using Mcp.Engineering.Openness;
using Mcp.Engineering.Sandbox;
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

    [McpServerTool(Name = "connect")]
    [Description("Attach to a running TIA session (sessionId) or open a project (projectPath; headless unless withUI=true). Provide exactly one.")]
    public CallToolResult Connect(
        [Description("TIA process id from list_sessions → attach mode.")] int? sessionId = null,
        [Description("Path to the .ap17 project file → open mode.")] string? projectPath = null,
        [Description("Open mode only: show the TIA UI. Default false (headless).")] bool withUI = false,
        [Description("Max seconds to wait for Openness startup. Default 60.")] int timeoutSeconds = 60)
        => Invoke("connect", () => _adapter.Connect(new ConnectOptions
        {
            SessionId = sessionId,
            ProjectPath = projectPath,
            WithUI = withUI,
            TimeoutSeconds = timeoutSeconds,
        }), ("projectPath", projectPath));

    [McpServerTool(Name = "disconnect")]
    [Description("Release project and portal handles. Never saves; never closes a project owned by an attached session. Reports unsaved changes.")]
    public CallToolResult Disconnect() => Invoke("disconnect", () => _adapter.Disconnect());

    [McpServerTool(Name = "save_project")]
    [Description("Explicitly save the open TIA project — the only tool that persists it.")]
    public CallToolResult SaveProject() => Invoke("save_project", () => { _adapter.SaveProject(); return new { }; });

    [McpServerTool(Name = "get_project_info")]
    [Description("Project name, path, PLC devices, block count, last modified (read-only).")]
    public CallToolResult GetProjectInfo() => Invoke("get_project_info", () => _adapter.GetProjectInfo());

    [McpServerTool(Name = "list_blocks")]
    [Description("Enumerate all blocks (OB/FB/FC/DB) incl. nested block groups (read-only).")]
    public CallToolResult ListBlocks(
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null)
        => Invoke("list_blocks", () => _adapter.ListBlocks(plcName));

    [McpServerTool(Name = "export_block")]
    [Description("Export a single block to XML under outputDir/Blocks|DB and upsert its record in outputDir/metadata.json (read-only w.r.t. the project).")]
    public CallToolResult ExportBlock(
        [Description("Block name as listed by list_blocks.")] string blockName,
        [Description("Export root directory.")] string outputDir)
        => Invoke("export_block", () => _adapter.ExportBlock(blockName, outputDir), ("outputDir", outputDir));

    [McpServerTool(Name = "export_all_blocks")]
    [Description("Export every PLC block to XML under outputDir (Blocks/ and DB/ subfolders; per-PLC subfolder when multiple PLCs) and write a metadata.json manifest per export root.")]
    public CallToolResult ExportAllBlocks(
        [Description("Export root directory for the XML files.")] string outputDir)
        => Invoke("export_all_blocks", () => _adapter.ExportAllBlocks(outputDir), ("outputDir", outputDir));

    [McpServerTool(Name = "export_tag_tables")]
    [Description("Export every PLC tag table to XML under outputDir/Tags (recursing nested groups) and upsert one metadata.json record per table. Per-PLC subfolder when the project has multiple PLCs, unless plcName is given.")]
    public CallToolResult ExportTagTables(
        [Description("Export root directory.")] string outputDir,
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null)
        => Invoke("export_tag_tables", () => _adapter.ExportTagTables(outputDir, plcName), ("outputDir", outputDir));

    [McpServerTool(Name = "export_udts")]
    [Description("Export every PLC data type (UDT) to XML under outputDir/UDT (recursing nested groups) and upsert one metadata.json record per type. Per-PLC subfolder when the project has multiple PLCs, unless plcName is given.")]
    public CallToolResult ExportUdts(
        [Description("Export root directory.")] string outputDir,
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null)
        => Invoke("export_udts", () => _adapter.ExportUdts(outputDir, plcName), ("outputDir", outputDir));

    [McpServerTool(Name = "sync_export")]
    [Description("Incrementally sync an export root with the current project state: PLC software-checksum gate (skip everything when unchanged), then a timestamp-nominated, hash-confirmed diff that re-exports only real changes and drops components deleted in TIA. Read-only w.r.t. the project. Run ingest_source afterwards to refresh the knowledge base.")]
    public CallToolResult SyncExport(
        [Description("Export root directory previously filled by export_all_blocks / export_tag_tables / export_udts.")] string outputDir,
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null)
        => Invoke("sync_export", () => _adapter.SyncExport(outputDir, plcName), ("outputDir", outputDir));

    [McpServerTool(Name = "import_block")]
    [Description("Import a modified block XML back into the project (DESTRUCTIVE: overwrites the block). Caller must validate the XML and snapshot the working folder first.")]
    public CallToolResult ImportBlock(
        [Description("Block name to overwrite.")] string blockName,
        [Description("Path to the modified XML file.")] string xmlFilePath)
        => Invoke("import_block", () => _adapter.ImportBlock(blockName, xmlFilePath), ("xmlFilePath", xmlFilePath));

    [McpServerTool(Name = "compile_block")]
    [Description("Compile the PLC software and report messages for the named block (write: mutates project compile state). V17 has no per-block compile — this is compile_plc + per-block filtering.")]
    public CallToolResult CompileBlock(
        [Description("Block name to report on.")] string blockName)
        => Invoke("compile_block", () => _adapter.CompileBlock(blockName));

    [McpServerTool(Name = "compile_plc")]
    [Description("Compile the whole PLC software, returning all messages (write: mutates project compile state).")]
    public CallToolResult CompilePlc() => Invoke("compile_plc", () => _adapter.CompilePlc());

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
            return ToolJson.Fail(OpennessErrorMapper.CodeFor(ex), ex.Message);
        }
    }

    private static string? Summarize((string Name, string? Value)[] pathArguments)
    {
        var paths = pathArguments.Where(argument => argument.Value != null).ToArray();
        return paths.Length == 0
            ? null
            : string.Join("; ", paths.Select(argument => $"{argument.Name}={argument.Value}"));
    }
}
