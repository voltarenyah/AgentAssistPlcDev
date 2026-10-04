using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Contracts.Engineering;

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
    string? ProjectLastModifiedBy,
    bool? IsSafetyDevice,
    string? FSignatureReadState,
    string? FSignature);

public sealed record OfflineBlockInfo(
    string Id,
    string Name,
    int? Number,
    string BlockType,
    string? ProgrammingLanguage,
    string? GroupPath,
    string RelativePath,
    bool Modified);

/// <summary>One exported PLC source object (block OB/FB/FC/DB, tag table, or UDT) from the
/// device manifest's "components" section. Falls back to block-crawl data (null metadata fields)
/// when the manifest is missing or legacy.</summary>
public sealed record SourceObjectInfo(
    string Id,
    string Name,
    int? Number,
    string Category,
    string? ProgrammingLanguage,
    string? GroupPath,
    string RelativePath,
    string? ContentHash,
    bool? IsKnowHowProtected,
    DateTimeOffset? ModifiedDate,
    string? Status,
    FingerprintSet? FingerprintComponents = null,
    /// <summary>Fingerprint-comparison kind, as the evidence model classifies it. `instance-db` is
    /// excluded from managed-source evidence, which is why such an object never acquires a stage
    /// baseline however often it is committed.</summary>
    string? EvidenceKind = null);

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
    IReadOnlyList<SourceObjectInfo> SourceObjects,
    int SourceObjectCount,
    IReadOnlyList<string> Diagnostics,
    DeviceExportMetadata? Device);

/// <summary>
/// The device snapshot's ingest implementation. Its readers used to be the device page's read path;
/// since ADR-0011 they are the only place the exported files are parsed — the projection ingests a
/// device from the manifest (with this class's block crawl as the fallback for a missing or legacy
/// manifest) and <c>DeviceSnapshotGraphReader</c> serves the routes from the graph. Nothing here runs
/// on a read request.
/// </summary>
public sealed class DeviceSnapshotReader
{
    /// <summary>The snapshot as this reader derives it from disk. Public because the projection ingests
    /// a device from the same readers and the ingest-inside-the-crawl tests exercise them directly; the
    /// device routes serve the graph (<c>DeviceSnapshotGraphReader</c>), not this method.</summary>
    public DeviceSnapshot Read(DeviceContext context, DeviceMetadata metadata)
    {
        var diagnostics = new List<string>();
        var blocks = ReadBlocks(context, diagnostics);
        var sourceObjects = ResolveSourceObjects(ReadManifestSourceObjects(context.SourceRoot), blocks);

        var state = ReadKnowledgeSnapshot(context, metadata);

        return new DeviceSnapshot(
            context.WorkbenchId,
            context.WorktreeId,
            context.DeviceId,
            metadata.PlcName,
            metadata.EngineeringIdentity,
            context.SourceRoot,
            context.KnowledgeDbPath,
            ReadSourceProjectPath(context),
            state,
            blocks,
            sourceObjects,
            blocks.Count,
            diagnostics,
            ReadDeviceExportMetadata(context));
    }

    /// <summary>
    /// The knowledge state and timestamp the device page shows: the database's existence and the
    /// persisted staleness flags decide it, and the projection stores exactly this value.
    /// </summary>
    public static DeviceKnowledgeSnapshot ReadKnowledgeSnapshot(DeviceContext context, DeviceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metadata);
        var state = !File.Exists(context.KnowledgeDbPath)
            ? "missing"
            : metadata.Knowledge.Stale || metadata.Knowledge.BaselineStale
                ? "stale"
                : "current";
        return new DeviceKnowledgeSnapshot(state, metadata.Knowledge.UpdatedAt);
    }

    /// <summary>
    /// Just the source objects a device exposes, resolved from disk. The task picker's route serves the
    /// same list from the graph; this manifest-or-crawl resolution is the ingest side of it.
    /// </summary>
    /// <param name="comparableOnly">Also drop the kinds the managed-source evidence domain excludes.
    /// A picker for a task's compare basis must not offer them: they can never carry a baseline, so a
    /// row for one could only ever say that it cannot be compared.</param>
    public IReadOnlyList<SourceObjectInfo> ReadSourceObjects(DeviceContext context, bool comparableOnly = false)
    {
        var manifest = ReadManifestSourceObjects(context.SourceRoot);
        var resolved = manifest.Count > 0
            ? manifest
            : ResolveSourceObjects(manifest, ReadBlocks(context, new List<string>()));
        return comparableOnly ? ComparableSourceObjects(resolved) : resolved;
    }

    /// <summary>
    /// The source objects a task can be compared against. Instance DBs are excluded: they are
    /// generated from their FB, which is where the information worth tracking lives, so the evidence
    /// domain excludes them (ADR-0001, the fingerprint-first compare design) and no commit can give
    /// one a baseline.
    /// </summary>
    public static IReadOnlyList<SourceObjectInfo> ComparableSourceObjects(IReadOnlyList<SourceObjectInfo> items) =>
        items.Where(item => !string.Equals(item.EvidenceKind, ManagedSourceEvidenceKind.InstanceDb, StringComparison.Ordinal))
            .ToArray();

    /// <summary>Manifest objects, or the block crawl when the manifest is missing or legacy. The
    /// fallback carries no manifest-only metadata (hashes, timestamps).</summary>
    private static IReadOnlyList<SourceObjectInfo> ResolveSourceObjects(
        IReadOnlyList<SourceObjectInfo> manifest,
        IReadOnlyList<OfflineBlockInfo> blocks) =>
        manifest.Count > 0
            ? manifest
            : CrawledSourceObjects(blocks);

    /// <summary>
    /// The block crawl as the ingest fallback for a manifest that is missing or legacy: the same
    /// objects <see cref="ReadBlocks"/> finds, with no manifest-only metadata. The projection uses it
    /// when it cannot read the manifest (ADR-0011, the crawl becomes the ingest fallback).
    /// </summary>
    public static IReadOnlyList<SourceObjectInfo> ReadCrawledSourceObjects(DeviceContext context, List<string> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diagnostics);
        return CrawledSourceObjects(ReadBlocks(context, diagnostics));
    }

    private static IReadOnlyList<SourceObjectInfo> CrawledSourceObjects(IReadOnlyList<OfflineBlockInfo> blocks) =>
        blocks.Select(block => new SourceObjectInfo(
            block.Id,
            block.Name,
            block.Number,
            block.BlockType,
            block.ProgrammingLanguage,
            block.GroupPath,
            block.RelativePath,
            null,
            null,
            null,
            null,
            null,
            CrawledEvidenceKind(block.BlockType))).ToArray();

    /// <summary>
    /// Classification of a crawl-derived object. The crawl knows only the XML element's generic block
    /// type — <c>SW.Blocks.InstanceDB</c> maps to the same "DB" as a global DB — and has no
    /// siemensTypeName to tell them apart, so a DB-category object is left <b>unclassified</b> (a null
    /// evidence kind) rather than claimed as a standard block: the picker excludes instance DBs by
    /// kind, and a wrong kind would silently offer an object that can never carry a baseline.
    /// </summary>
    private static string? CrawledEvidenceKind(string blockType) =>
        string.Equals(blockType, "DB", StringComparison.Ordinal) ? null : ManagedSourceEvidenceKind.StandardBlock;

    /// <summary>The manifest's "device" section — tolerant read: missing/legacy manifest, missing
    /// property, or unparseable JSON all degrade to null. Source discovery never depends on
    /// this optional export metadata. Public because the projection stores it as device facts.</summary>
    public static DeviceExportMetadata? ReadDeviceExportMetadata(DeviceContext context)
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
                ReadString(device, "projectLastModifiedBy"),
                ReadBool(device, "isSafetyDevice"),
                ReadString(device, "fSignatureReadState"),
                ReadString(device, "fSignature"));
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

    /// <summary>Manifest "components" section — tolerant read like the device section: a missing
    /// or legacy manifest, missing property, or unparseable JSON all degrade to an empty list and
    /// callers fall back to the block crawl. Public so the workbench coordinator resolves
    /// source-object identities (name/category per relativePath) from the same data.</summary>
    public static IReadOnlyList<SourceObjectInfo> ReadManifestSourceObjects(string sourceRoot)
    {
        var manifestPath = Path.Combine(sourceRoot, "metadata.json");
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (manifest.RootElement.ValueKind != JsonValueKind.Object
                || !manifest.RootElement.TryGetProperty("components", out var components)
                || components.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var objects = new List<SourceObjectInfo>();
            foreach (var component in components.EnumerateArray())
            {
                if (component.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var exportedFile = ReadString(component, "exportedFile");
                var name = ReadString(component, "name");
                var category = ReadString(component, "category");
                if (string.IsNullOrWhiteSpace(exportedFile)
                    || string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(category))
                {
                    // Failed exports carry no file and are not openable/comparable objects.
                    continue;
                }

                var relativePath = exportedFile.Replace('\\', '/');
                var fingerprintComponents = ReadFingerprintSet(component, "fingerprints");
                if (fingerprintComponents is null
                    && component.TryGetProperty("fingerprints", out var legacyFingerprints)
                    && legacyFingerprints.ValueKind == JsonValueKind.String)
                {
                    fingerprintComponents = FingerprintSet.Parse(legacyFingerprints.GetString());
                }
                objects.Add(new SourceObjectInfo(
                    ReadString(component, "id") ?? $"source:{relativePath}",
                    name,
                    ReadInt(component, "number"),
                    category,
                    ReadString(component, "programmingLanguage"),
                    SourceGroupPath(relativePath),
                    relativePath,
                    ReadString(component, "contentHash"),
                    ReadBool(component, "isKnowHowProtected"),
                    ReadDate(component, "modifiedDate"),
                    ReadString(component, "status"),
                    fingerprintComponents,
                    CommittedSourceManifest.EvidenceKindOf(category, ReadString(component, "siemensTypeName"))));
            }

            return objects
                .OrderBy(item => item.Category, StringComparer.Ordinal)
                .ThenBy(item => item.Number ?? int.MaxValue)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private static int? ReadInt(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;

    private static FingerprintSet? ReadFingerprintSet(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new FingerprintSet();
        foreach (var item in value.EnumerateObject())
        {
            if (item.Value.ValueKind == JsonValueKind.String && item.Value.GetString() is { } fingerprint)
            {
                result[item.Name] = fingerprint;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static bool? ReadBool(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>The worktree's source project path, or null when the worktree metadata is absent or
    /// unreadable. Public because the projection stores it as a device fact.</summary>
    public static string? ReadSourceProjectPath(DeviceContext context)
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
                if (!IsBlockCategoryPath(relativePath))
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

    /// <summary>
    /// True for a block-category exported path: the <c>Blocks/</c> and <c>DB/</c> subtree. It is the
    /// crawl's own rule, and the device page's block list and count stay that subset.
    /// </summary>
    public static bool IsBlockCategoryPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        return normalized.StartsWith("Blocks/", StringComparison.Ordinal)
            || normalized.StartsWith("DB/", StringComparison.Ordinal);
    }

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
