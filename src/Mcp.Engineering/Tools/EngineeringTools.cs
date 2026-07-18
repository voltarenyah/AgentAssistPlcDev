using System.ComponentModel;
using Contracts;
using Contracts.Engineering;
using Mcp.Engineering.Adapter;
using Mcp.Engineering.Openness;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Engineering.Tools;

/// <summary>
/// MCP tool surface (mcp-engineering.md §3). Thin wrappers over <see cref="IEngineeringPlatform"/>
/// (registered as <see cref="StaAdapter"/> so all Openness calls run on one STA thread);
/// all failures become structured isError results via <see cref="ToolJson"/>.
/// </summary>
[McpServerToolType]
public sealed class EngineeringTools
{
    private readonly IEngineeringPlatform _adapter;

    public EngineeringTools(IEngineeringPlatform adapter) => _adapter = adapter;

    [McpServerTool(Name = "check_environment")]
    [Description("Validate TIA Openness installation, DLL paths, and user group membership (read-only).")]
    public CallToolResult CheckEnvironment() => Invoke(() => _adapter.CheckEnvironment());

    [McpServerTool(Name = "list_sessions")]
    [Description("Enumerate running TIA Portal processes that can be attached to (read-only).")]
    public CallToolResult ListSessions() => Invoke(() => _adapter.ListSessions());

    [McpServerTool(Name = "connect")]
    [Description("Attach to a running TIA session (sessionId) or open a project (projectPath; headless unless withUI=true). Provide exactly one.")]
    public CallToolResult Connect(
        [Description("TIA process id from list_sessions → attach mode.")] int? sessionId = null,
        [Description("Path to the .ap17 project file → open mode.")] string? projectPath = null,
        [Description("Open mode only: show the TIA UI. Default false (headless).")] bool withUI = false,
        [Description("Max seconds to wait for Openness startup. Default 60.")] int timeoutSeconds = 60)
        => Invoke(() => _adapter.Connect(new ConnectOptions
        {
            SessionId = sessionId,
            ProjectPath = projectPath,
            WithUI = withUI,
            TimeoutSeconds = timeoutSeconds,
        }));

    [McpServerTool(Name = "disconnect")]
    [Description("Release project and portal handles. Never saves; never closes a project owned by an attached session. Reports unsaved changes.")]
    public CallToolResult Disconnect() => Invoke(() => _adapter.Disconnect());

    [McpServerTool(Name = "save_project")]
    [Description("Explicitly save the open TIA project — the only tool that persists it.")]
    public CallToolResult SaveProject() => Invoke(() => { _adapter.SaveProject(); return new { }; });

    [McpServerTool(Name = "get_project_info")]
    [Description("Project name, path, PLC devices, block count, last modified (read-only).")]
    public CallToolResult GetProjectInfo() => Invoke(() => _adapter.GetProjectInfo());

    [McpServerTool(Name = "list_blocks")]
    [Description("Enumerate all blocks (OB/FB/FC/DB) incl. nested block groups (read-only).")]
    public CallToolResult ListBlocks(
        [Description("PLC device name; optional for single-PLC projects.")] string? plcName = null)
        => Invoke(() => _adapter.ListBlocks(plcName));

    [McpServerTool(Name = "export_block")]
    [Description("Export a single block to XML under outputDir/Blocks|DB and upsert its record in outputDir/metadata.json (read-only w.r.t. the project).")]
    public CallToolResult ExportBlock(
        [Description("Block name as listed by list_blocks.")] string blockName,
        [Description("Export root directory.")] string outputDir)
        => Invoke(() => _adapter.ExportBlock(blockName, outputDir));

    [McpServerTool(Name = "export_all_blocks")]
    [Description("Export every PLC block to XML under outputDir (Blocks/ and DB/ subfolders; per-PLC subfolder when multiple PLCs) and write a metadata.json manifest per export root.")]
    public CallToolResult ExportAllBlocks(
        [Description("Export root directory for the XML files.")] string outputDir)
        => Invoke(() => _adapter.ExportAllBlocks(outputDir));

    [McpServerTool(Name = "import_block")]
    [Description("Import a modified block XML back into the project (DESTRUCTIVE: overwrites the block). Caller must validate the XML and snapshot the working folder first.")]
    public CallToolResult ImportBlock(
        [Description("Block name to overwrite.")] string blockName,
        [Description("Path to the modified XML file.")] string xmlFilePath)
        => Invoke(() => _adapter.ImportBlock(blockName, xmlFilePath));

    [McpServerTool(Name = "compile_block")]
    [Description("Compile the PLC software and report messages for the named block (write: mutates project compile state). V17 has no per-block compile — this is compile_plc + per-block filtering.")]
    public CallToolResult CompileBlock(
        [Description("Block name to report on.")] string blockName)
        => Invoke(() => _adapter.CompileBlock(blockName));

    [McpServerTool(Name = "compile_plc")]
    [Description("Compile the whole PLC software, returning all messages (write: mutates project compile state).")]
    public CallToolResult CompilePlc() => Invoke(() => _adapter.CompilePlc());

    private CallToolResult Invoke(Func<object> action)
    {
        try
        {
            return ToolJson.Ok(action());
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
}
