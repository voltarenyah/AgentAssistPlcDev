using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Agent.Workbench;

public sealed record DeviceKnowledgeSnapshot(string State, string? UpdatedAt);

public sealed record OfflineBlockInfo(
    string Id,
    string Name,
    int? Number,
    string BlockType,
    string? ProgrammingLanguage,
    string? GroupPath,
    string RelativePath,
    bool Modified);

public sealed record DeviceSnapshot(
    string WorkbenchId,
    string WorktreeId,
    string DeviceId,
    string PlcName,
    string EngineeringIdentity,
    string ExportedSourceRoot,
    string ModifiedSourceRoot,
    string KnowledgeDbPath,
    string? SourceProjectPath,
    DeviceKnowledgeSnapshot Knowledge,
    IReadOnlyList<OfflineBlockInfo> Blocks,
    int OverlayCount,
    IReadOnlyList<string> Diagnostics);

public sealed class DeviceSnapshotReader
{
    public DeviceSnapshot Read(DeviceContext context, DeviceMetadata metadata)
    {
        var diagnostics = new List<string>();
        var blocks = ReadBlocks(context, diagnostics, out var overlayCount);
        var state = !File.Exists(context.KnowledgeDbPath)
            ? "missing"
            : metadata.Knowledge.Stale || metadata.Knowledge.BaselineStale
                ? "stale"
                : "current";

        return new DeviceSnapshot(
            context.WorkbenchId,
            context.WorktreeId,
            context.DeviceId,
            metadata.PlcName,
            metadata.EngineeringIdentity,
            context.ExportedSourceRoot,
            context.ModifiedSourceRoot,
            context.KnowledgeDbPath,
            ReadSourceProjectPath(context),
            new DeviceKnowledgeSnapshot(state, metadata.Knowledge.UpdatedAt),
            blocks,
            overlayCount,
            diagnostics);
    }

    private static string? ReadSourceProjectPath(DeviceContext context)
    {
        var metadataPath = Path.Combine(context.WorktreeRoot, "worktree.json");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            return new AtomicJsonStore().Read<WorktreeMetadata>(metadataPath).SourceProjectPath;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<OfflineBlockInfo> ReadBlocks(
        DeviceContext context,
        List<string> diagnostics,
        out int overlayCount)
    {
        overlayCount = Directory.Exists(context.ModifiedSourceRoot)
            ? Directory.EnumerateFiles(context.ModifiedSourceRoot, "*.xml", SearchOption.AllDirectories).Count()
            : 0;

        var manifestPath = Path.Combine(context.ExportedSourceRoot, "metadata.json");
        if (!File.Exists(manifestPath))
        {
            diagnostics.Add($"Export manifest is missing: {manifestPath}");
            return [];
        }

        JsonDocument manifest;
        try
        {
            manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        }
        catch (JsonException ex)
        {
            diagnostics.Add($"Export manifest is invalid JSON: {manifestPath}: {ex.Message}");
            return [];
        }

        using (manifest)
        {
            if (manifest.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add($"Export manifest root must be a JSON object: {manifestPath}");
                return [];
            }

            if (!manifest.RootElement.TryGetProperty("components", out var components)
                || components.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add($"Export manifest has no components array: {manifestPath}");
                return [];
            }

            var blocks = new List<OfflineBlockInfo>();
            var representedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var componentIndex = -1;
            foreach (var component in components.EnumerateArray())
            {
                componentIndex++;
                if (component.ValueKind != JsonValueKind.Object)
                {
                    diagnostics.Add(
                        $"Export manifest component at index {componentIndex} must be a JSON object.");
                    continue;
                }

                var exportedFile = ReadString(component, "exportedFile");
                if (string.IsNullOrWhiteSpace(exportedFile))
                    continue;

                string normalizedPath;
                try
                {
                    var fullPath = WorkbenchPaths.ResolveRelative(context.ExportedSourceRoot, exportedFile);
                    normalizedPath = Path.GetRelativePath(context.ExportedSourceRoot, fullPath).Replace('\\', '/');
                }
                catch (Exception ex) when (ex is ArgumentException or WorkbenchPathException)
                {
                    diagnostics.Add($"Manifest component has invalid exportedFile '{exportedFile}': {ex.Message}");
                    continue;
                }

                representedPaths.Add(normalizedPath);
                var category = ReadString(component, "category");
                var status = ReadString(component, "status");
                if (!IsBlockCategory(category)
                    || !string.Equals(status, "Exported", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var sourcePath = ReadString(component, "sourcePath");
                var baselineBlock = new OfflineBlockInfo(
                    ReadString(component, "id") ?? $"{category}:{sourcePath ?? normalizedPath}",
                    ReadString(component, "name") ?? Path.GetFileNameWithoutExtension(normalizedPath),
                    ReadInt32(component, "number"),
                    category!,
                    ReadString(component, "programmingLanguage"),
                    GroupPathOf(sourcePath),
                    normalizedPath,
                    false);
                var modifiedPath = WorkbenchPaths.ResolveRelative(
                    context.ModifiedSourceRoot,
                    normalizedPath);
                var modified = File.Exists(modifiedPath);
                if (modified)
                {
                    if (TryReadOverlayBlock(
                        modifiedPath,
                        normalizedPath,
                        out var effectiveBlock,
                        out var overlayError))
                    {
                        blocks.Add(effectiveBlock! with
                        {
                            Id = ReadString(component, "id")
                                ?? effectiveBlock!.Id,
                        });
                    }
                    else
                    {
                        diagnostics.Add(
                            $"Overlay '{normalizedPath}' is not a supported Siemens PLC block: {overlayError}");
                        blocks.Add(baselineBlock);
                    }

                    continue;
                }

                blocks.Add(baselineBlock);
            }

            if (Directory.Exists(context.ModifiedSourceRoot))
            {
                foreach (var overlayPath in Directory.EnumerateFiles(
                    context.ModifiedSourceRoot,
                    "*.xml",
                    SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(context.ModifiedSourceRoot, overlayPath)
                        .Replace('\\', '/');
                    if (representedPaths.Contains(relativePath))
                        continue;

                    if (TryReadOverlayBlock(overlayPath, relativePath, out var block, out var error))
                        blocks.Add(block!);
                    else
                        diagnostics.Add($"Overlay '{relativePath}' is not a supported Siemens PLC block: {error}");
                }
            }

            return blocks
                .OrderBy(block => block.BlockType, StringComparer.Ordinal)
                .ThenBy(block => block.Number ?? int.MaxValue)
                .ThenBy(block => block.Name, StringComparer.Ordinal)
                .ThenBy(block => block.RelativePath, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private static bool TryReadOverlayBlock(
        string path,
        string relativePath,
        out OfflineBlockInfo? result,
        out string error)
    {
        result = null;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(path, settings);
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName != "Document")
            {
                error = "expected a Siemens Document root";
                return false;
            }

            var supportedElements = document.Root.Descendants()
                .Where(candidate => BlockTypeOf(candidate.Name.LocalName) is not null)
                .ToArray();
            if (supportedElements.Length != 1 || supportedElements[0].Parent != document.Root)
            {
                error = "expected exactly one direct supported block element below the Siemens Document root";
                return false;
            }

            var element = supportedElements[0];
            var blockType = BlockTypeOf(element.Name.LocalName)!;
            var attributes = element.Elements()
                .FirstOrDefault(candidate => candidate.Name.LocalName == "AttributeList");
            var name = AttributeValue(attributes, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "the block AttributeList has no Name";
                return false;
            }

            var number = int.TryParse(AttributeValue(attributes, "Number"), out var parsedNumber)
                ? parsedNumber
                : (int?)null;
            result = new OfflineBlockInfo(
                $"overlay:{relativePath}",
                name,
                number,
                blockType,
                AttributeValue(attributes, "ProgrammingLanguage"),
                OverlayGroupPath(relativePath),
                relativePath,
                true);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? ReadString(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool IsBlockCategory(string? category) =>
        category is "OB" or "FB" or "FC" or "DB";

    private static string? GroupPathOf(string? sourcePath)
    {
        var separator = sourcePath?.LastIndexOf('/') ?? -1;
        return separator <= 0 ? null : sourcePath![..separator];
    }

    private static string? OverlayGroupPath(string relativePath)
    {
        var segments = relativePath.Split('/');
        return segments.Length <= 2 ? null : string.Join('/', segments[1..^1]);
    }

    private static string? BlockTypeOf(string elementName) => elementName switch
    {
        "SW.Blocks.OB" => "OB",
        "SW.Blocks.FB" => "FB",
        "SW.Blocks.FC" => "FC",
        "SW.Blocks.DB" or "SW.Blocks.GlobalDB" or "SW.Blocks.InstanceDB" or "SW.Blocks.ArrayDB" => "DB",
        _ => null,
    };

    private static string? AttributeValue(XElement? attributes, string name) =>
        attributes?.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
}
