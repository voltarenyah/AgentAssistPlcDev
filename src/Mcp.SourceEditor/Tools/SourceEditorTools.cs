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
    private readonly PathJail writeJail;

    public SourceEditorTools(SourceEditorService service, PathJail writeJail)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.writeJail = writeJail ?? throw new ArgumentNullException(nameof(writeJail));
    }

    [McpServerTool(Name = "src_parse_block")]
    [Description("Inspect a TIA block XML file without modifying it. Returns stable network XML IDs, document-order network numbers, cultures, and editable fields.")]
    public CallToolResult ParseBlock([Description("Path to a TIA block XML file inside an allowed sandbox root.")] string xmlFilePath) =>
        Invoke(() => service.Parse(xmlFilePath));

    [McpServerTool(Name = "src_preview_edits")]
    [Description("Apply typed title/comment/safe-property edits to a new XML file under modified-source. Does not import into TIA and never changes protected PLC logic.")]
    public CallToolResult PreviewEdits(string xmlFilePath, SourceEdit[] edits,
        string? outputFilePath = null, bool overwriteOutput = false) =>
        Invoke(() => service.Preview(
            xmlFilePath,
            edits,
            ValidateWriteTarget(outputFilePath),
            overwriteOutput));

    [McpServerTool(Name = "src_apply_edits")]
    [Description("Apply typed edits to an XML file under modified-source, or atomically replace an effective overlay only when both inPlace and confirmInPlace are true. Does not import into TIA.")]
    public CallToolResult ApplyEdits(string xmlFilePath, SourceEdit[] edits,
        string? outputFilePath = null, bool overwriteOutput = false, bool inPlace = false, bool confirmInPlace = false) =>
        Invoke(() =>
        {
            var writeTarget = ValidateWriteTarget(
                inPlace
                    ? outputFilePath ?? xmlFilePath
                    : outputFilePath);

            return service.Apply(
                xmlFilePath,
                edits,
                writeTarget,
                overwriteOutput,
                inPlace,
                confirmInPlace);
        });

    [McpServerTool(Name = "src_diff")]
    [Description("Compare two TIA XML files, reporting editable-field changes and whether protected PLC logic/structure matches.")]
    public CallToolResult Diff(string originalFilePath, string modifiedFilePath) =>
        Invoke(() => service.Diff(originalFilePath, modifiedFilePath));

    [McpServerTool(Name = "src_validate")]
    [Description("Validate a TIA block XML file. With baselineFilePath, proves protected PLC logic and structure did not change.")]
    public CallToolResult Validate(string xmlFilePath, string? baselineFilePath = null) =>
        Invoke(() => service.Validate(xmlFilePath, baselineFilePath));

    private string ValidateWriteTarget(string? outputFilePath)
    {
        if (string.IsNullOrWhiteSpace(outputFilePath))
        {
            throw new SandboxException(
                "SANDBOX_PATH_DENIED",
                "outputFilePath is required and must be inside modified-source.",
                "Prepare a device overlay and pass its path as outputFilePath.");
        }

        var output = writeJail.Validate(outputFilePath, nameof(outputFilePath));
        var directory = Path.GetDirectoryName(output);
        var insideModifiedSource = false;

        while (!string.IsNullOrEmpty(directory))
        {
            if (string.Equals(
                    Path.GetFileName(directory),
                    "modified-source",
                    StringComparison.OrdinalIgnoreCase))
            {
                insideModifiedSource = true;
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        if (!insideModifiedSource)
        {
            throw new SandboxException(
                "SANDBOX_PATH_DENIED",
                $"outputFilePath must be inside an allowed modified-source root: {output}",
                "Use DeviceSourceResolver.PrepareEditable and pass the returned path.");
        }

        RejectExistingReparsePoints(output);
        return output;
    }

    private static void RejectExistingReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new SandboxException(
                "SANDBOX_PATH_DENIED",
                $"outputFilePath has no filesystem root: {fullPath}");
        }

        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(
                     Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                continue;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new SandboxException(
                    "SANDBOX_PATH_DENIED",
                    $"outputFilePath traverses reparse point '{current}'.",
                    "Choose a local path beneath the device modified-source root.");
            }
        }
    }

    private static CallToolResult Invoke(Func<object> action)
    {
        try { return ToolJson.Ok(action()); }
        catch (SourceEditorException ex) { return ToolJson.Fail(ex.Code, ex.Message, ex.Remediation, ex.BatchIndex); }
        catch (SandboxException ex) { return ToolJson.Fail("SOURCE_PATH_DENIED", ex.Message, ex.Remediation); }
        catch (Exception ex) { return ToolJson.Fail("UNEXPECTED_ERROR", ex.Message); }
    }
}
