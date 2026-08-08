using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Agent.Workbench;

public sealed record DeviceKnowledgeSnapshot(string State, string? UpdatedAt);

/// <summary>Project/device identity captured by mcp-engineering into the export manifest's
/// additive "device" section (buildnote/plan/export-sync.md §2, 2026-07-31). Null on legacy
/// manifests (pre-feature exports) — the UI hides the section then.</summary>
public sealed record DeviceExportMetadata(
    string? PlcName,
    string? DeviceName,
    string? TypeIdentifier,
    string? ProjectName,
    string? ProjectAuthor,
    string? ProjectComment,
    string? ProjectVersion,
    string? ProjectCopyright,
    DateTimeOffset? ProjectCreationTime,
    DateTimeOffset? ProjectLastModified,
    string? ProjectLastModifiedBy);

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
    string SourceRoot,
    string KnowledgeDbPath,
    string? SourceProjectPath,
    DeviceKnowledgeSnapshot Knowledge,
    IReadOnlyList<OfflineBlockInfo> Blocks,
    int SourceObjectCount,
    IReadOnlyList<string> Diagnostics,
    DeviceExportMetadata? Device);

public sealed class DeviceSnapshotReader
{
    public DeviceSnapshot Read(DeviceContext context, DeviceMetadata metadata)
    {
        var diagnostics = new List<string>();
        var blocks = ReadBlocks(context, diagnostics);
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
            context.SourceRoot,
            context.KnowledgeDbPath,
            ReadSourceProjectPath(context),
            new DeviceKnowledgeSnapshot(state, metadata.Knowledge.UpdatedAt),
            blocks,
            blocks.Count,
            diagnostics,
            ReadDeviceExportMetadata(context));
    }

    /// <summary>Manifest "device" section — tolerant read: missing/legacy manifest, missing
    /// property, or unparseable JSON all degrade to null. Source discovery never depends on
    /// this optional export metadata.</summary>
    private static DeviceExportMetadata? ReadDeviceExportMetadata(DeviceContext context)
    {
        var manifestPath = Path.Combine(context.SourceRoot, "metadata.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (manifest.RootElement.ValueKind != JsonValueKind.Object
                || !manifest.RootElement.TryGetProperty("device", out var device)
                || device.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new DeviceExportMetadata(
                ReadString(device, "plcName"),
                ReadString(device, "deviceName"),
                ReadString(device, "typeIdentifier"),
                ReadString(device, "projectName"),
                ReadString(device, "projectAuthor"),
                ReadString(device, "projectComment"),
                ReadString(device, "projectVersion"),
                ReadString(device, "projectCopyright"),
                ReadDate(device, "projectCreationTime"),
                ReadDate(device, "projectLastModified"),
                ReadString(device, "projectLastModifiedBy"));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadDate(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var date)
            ? date
            : null;

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
        List<string> diagnostics)
    {
        string sourceRoot;
        try
        {
            sourceRoot = WorkbenchPaths.ValidateResolvedRoot(context.SourceRoot);
        }
        catch (Exception ex) when (
            ex is WorkbenchPathException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            diagnostics.Add($"PLC source root '{context.SourceRoot}' was rejected: {ex.Message}");
            return [];
        }

        if (!Directory.Exists(sourceRoot))
            return [];

        var blocks = new List<OfflineBlockInfo>();
        var pending = new Queue<string>();
        pending.Enqueue(sourceRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                var relativeDirectory = Path.GetRelativePath(sourceRoot, directory).Replace('\\', '/');
                diagnostics.Add($"PLC source path '{relativeDirectory}' could not be read: {ex.Message}");
                continue;
            }

            foreach (var entry in entries.OrderBy(path => path, StringComparer.Ordinal))
            {
                var relativePath = Path.GetRelativePath(sourceRoot, entry).Replace('\\', '/');
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (
                    ex is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
                {
                    diagnostics.Add($"PLC source path '{relativePath}' could not be validated: {ex.Message}");
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    diagnostics.Add($"PLC source path '{relativePath}' was rejected because it is a reparse point.");
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Enqueue(entry);
                    continue;
                }

                if (!string.Equals(Path.GetExtension(entry), ".xml", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Only block categories (Blocks/, DB/) are parsed into block info. Tags/ and
                // UDT/ exports are valid XML but not blocks — reading them as blocks produced
                // one spurious "malformed or unsupported" diagnostic per file per snapshot.
                var slash = relativePath.Replace('\\', '/');
                if (!slash.StartsWith("Blocks/", StringComparison.Ordinal)
                    && !slash.StartsWith("DB/", StringComparison.Ordinal))
                    continue;

                try
                {
                    _ = WorkbenchPaths.ResolveRelativeBelowValidatedRoot(sourceRoot, relativePath);
                }
                catch (Exception ex) when (ex is ArgumentException or WorkbenchPathException)
                {
                    diagnostics.Add($"PLC source path '{relativePath}' was rejected: {ex.Message}");
                    continue;
                }

                if (TryReadSourceBlock(entry, relativePath, out var block, out var error))
                    blocks.Add(block!);
                else
                    diagnostics.Add($"PLC source XML '{relativePath}' is malformed or unsupported: {error}");
            }
        }

        return blocks
            .OrderBy(block => block.BlockType, StringComparer.Ordinal)
            .ThenBy(block => block.Number ?? int.MaxValue)
            .ThenBy(block => block.Name, StringComparer.Ordinal)
            .ThenBy(block => block.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryReadSourceBlock(
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
            var (filenameName, filenameNumber) = FilenameIdentity(relativePath, blockType);
            var name = AttributeValue(attributes, "Name");
            if (string.IsNullOrWhiteSpace(name))
                name = filenameName;

            var number = int.TryParse(AttributeValue(attributes, "Number"), out var parsedNumber)
                ? parsedNumber
                : filenameNumber;
            result = new OfflineBlockInfo(
                $"source:{relativePath}",
                name,
                number,
                blockType,
                AttributeValue(attributes, "ProgrammingLanguage"),
                SourceGroupPath(relativePath),
                relativePath,
                false);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (
            ex is XmlException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? ReadString(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? SourceGroupPath(string relativePath)
    {
        var segments = relativePath.Split('/');
        return segments.Length <= 2 ? null : string.Join('/', segments[1..^1]);
    }

    private static (string Name, int? Number) FilenameIdentity(
        string relativePath,
        string blockType)
    {
        var filename = Path.GetFileNameWithoutExtension(relativePath);
        var suffixStart = filename.LastIndexOf(" [", StringComparison.Ordinal);
        if (suffixStart < 0 || !filename.EndsWith(']'))
            return (filename, null);

        var suffix = filename[(suffixStart + 2)..^1];
        if (!suffix.StartsWith(blockType, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(suffix[blockType.Length..], out var number))
        {
            return (filename, null);
        }

        return (filename[..suffixStart], number);
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
