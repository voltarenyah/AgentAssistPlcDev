using System.ComponentModel;
using Contracts.Sandbox;
using Mcp.SourceEditor.Models;
using Mcp.SourceEditor.Xml;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.SourceEditor.Tools;

[McpServerToolType]
public sealed class SourceEditorTools
{
    private readonly SourceEditorService service;
    public SourceEditorTools(SourceEditorService service) => this.service = service;

    [McpServerTool(Name = "src_parse_block")]
    [Description("Inspect a TIA block XML file without modifying it. Returns stable network XML IDs, document-order network numbers, cultures, and editable fields.")]
    public CallToolResult ParseBlock([Description("Path to a TIA block XML file inside an allowed sandbox root.")] string xmlFilePath) =>
        Invoke(() => service.Parse(xmlFilePath));

    [McpServerTool(Name = "src_preview_edits")]
    [Description("Apply typed title/comment/safe-property edits to a new preview XML file. Does not import into TIA and never changes protected PLC logic.")]
    public CallToolResult PreviewEdits(string xmlFilePath, SourceEdit[] edits,
        string? outputFilePath = null, bool overwriteOutput = false) =>
        Invoke(() => service.Preview(xmlFilePath, edits, outputFilePath, overwriteOutput));

    [McpServerTool(Name = "src_apply_edits")]
    [Description("Apply typed edits to a sibling XML file, or atomically replace the source only when both inPlace and confirmInPlace are true. Does not import into TIA.")]
    public CallToolResult ApplyEdits(string xmlFilePath, SourceEdit[] edits,
        string? outputFilePath = null, bool overwriteOutput = false, bool inPlace = false, bool confirmInPlace = false) =>
        Invoke(() => service.Apply(xmlFilePath, edits, outputFilePath, overwriteOutput, inPlace, confirmInPlace));

    [McpServerTool(Name = "src_diff")]
    [Description("Compare two TIA XML files, reporting editable-field changes and whether protected PLC logic/structure matches.")]
    public CallToolResult Diff(string originalFilePath, string modifiedFilePath) =>
        Invoke(() => service.Diff(originalFilePath, modifiedFilePath));

    [McpServerTool(Name = "src_validate")]
    [Description("Validate a TIA block XML file. With baselineFilePath, proves protected PLC logic and structure did not change.")]
    public CallToolResult Validate(string xmlFilePath, string? baselineFilePath = null) =>
        Invoke(() => service.Validate(xmlFilePath, baselineFilePath));

    private static CallToolResult Invoke(Func<object> action)
    {
        try { return ToolJson.Ok(action()); }
        catch (SourceEditorException ex) { return ToolJson.Fail(ex.Code, ex.Message, ex.Remediation, ex.BatchIndex); }
        catch (SandboxException ex) { return ToolJson.Fail("SOURCE_PATH_DENIED", ex.Message, ex.Remediation); }
        catch (Exception ex) { return ToolJson.Fail("UNEXPECTED_ERROR", ex.Message); }
    }
}
