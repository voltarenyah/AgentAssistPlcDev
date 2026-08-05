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

    [McpServerTool(Name = "src_apply_edits")]
    [Description("Apply typed edits to checked-out device source XML. Current app calls must pass the exact devices/<device>/source root; omitting sourceRoot is supported only for legacy modified-source output. Does not import into TIA.")]
    public CallToolResult ApplyEdits(string xmlFilePath, SourceEdit[] edits,
        string? outputFilePath = null, bool overwriteOutput = false, bool inPlace = false,
        bool confirmInPlace = false, string? sourceRoot = null) =>
        Invoke(() =>
        {
            var writeTarget = ValidateWriteTarget(
                inPlace
                    ? outputFilePath ?? xmlFilePath
                    : outputFilePath,
                sourceRoot);

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

    private string ValidateWriteTarget(string? outputFilePath, string? sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(outputFilePath))
        {
            throw new SandboxException(
                "SANDBOX_PATH_DENIED",
                "outputFilePath is required.",
                "Pass the selected device source path as outputFilePath.");
        }

        return sourceRoot is null
            ? ValidateLegacyModifiedSourceWriteTarget(outputFilePath)
            : ValidateCurrentSourceWriteTarget(outputFilePath, sourceRoot);
    }

    private string ValidateCurrentSourceWriteTarget(string outputFilePath, string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            throw new SandboxException(
                "SANDBOX_PATH_DENIED",
                "sourceRoot must identify the selected device source root.");
        }

        var trustedRoot = writeJail.Validate(sourceRoot, nameof(sourceRoot))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        RejectExistingReparsePoints(trustedRoot, nameof(sourceRoot));
        RequireDeviceSourceRootShape(trustedRoot);
        if (!Directory.Exists(trustedRoot))
        {
            throw new SandboxException(
                "SANDBOX_PATH_DENIED",
                $"sourceRoot does not exist: {trustedRoot}");
        }

        var output = writeJail.Validate(outputFilePath, nameof(outputFilePath));
        RejectExistingReparsePoints(output, nameof(outputFilePath));
        if (!IsBelow(trustedRoot, output))
        {
            throw new SandboxException(
                "SANDBOX_PATH_DENIED",
                $"outputFilePath must be below the selected sourceRoot: {output}",
                "Use the exact source path bound by the selected device context.");
        }

        return output;
    }

    private string ValidateLegacyModifiedSourceWriteTarget(string outputFilePath)
    {
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
                $"Legacy outputFilePath must be inside modified-source: {output}",
                "Current app calls must pass sourceRoot and write in place.");
        }

        RejectExistingReparsePoints(output, nameof(outputFilePath));
        return output;
    }

    private static void RequireDeviceSourceRootShape(string sourceRoot)
    {
        var sourceDirectory = new DirectoryInfo(sourceRoot);
        var deviceDirectory = sourceDirectory.Parent;
        var devicesDirectory = deviceDirectory?.Parent;
        if (!string.Equals(sourceDirectory.Name, "source", StringComparison.OrdinalIgnoreCase)
            || deviceDirectory is null
            || string.IsNullOrWhiteSpace(deviceDirectory.Name)
            || !string.Equals(devicesDirectory?.Name, "devices", StringComparison.OrdinalIgnoreCase))
        {
            throw new SandboxException(
                "SANDBOX_PATH_DENIED",
                $"sourceRoot must have the devices/<device>/source shape: {sourceRoot}");
        }
    }

    private static bool IsBelow(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, ".", StringComparison.Ordinal)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void RejectExistingReparsePoints(string path, string parameterName)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
                throw new SandboxException(
                    "SANDBOX_PATH_DENIED",
                    $"{parameterName} has no filesystem root: {fullPath}");
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
                    $"{parameterName} traverses reparse point '{current}'.",
                    "Choose a local path beneath the permitted device source root.");
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
