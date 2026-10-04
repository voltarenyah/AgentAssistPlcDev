using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Workbench;
using Contracts.Engineering;

namespace Agent.Workbench.EngineeringGraph;

/// <summary>What one device projection observed and wrote. <see cref="PropertyRowsWritten"/> is the
/// number AC-003 reports: it is zero when the second ingest finds nothing changed.
/// <see cref="Diagnostics"/> is the ingest's own report; the device page shows the diagnostics the
/// source resolution produced (<see cref="StoredDiagnostics"/>), which is what it showed before the
/// projection existed (AC-002).</summary>
public sealed record DeviceProjectionResult(
    string DeviceId,
    string ManifestDigest,
    bool UsedCrawlFallback,
    int SourceObjectCount,
    int BlockCount,
    GraphPropertyWriteResult Write,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> StoredDiagnostics)
{
    public int PropertyRowsWritten => Write.PropertyRowsWritten;
}

/// <summary>
/// Projects one device's exported facts into the engineering graph as node properties (ADR-0011,
/// ADR-0012). The device manifest is the ingest source; the block crawl runs only when the manifest
/// is missing or legacy. Everything filesystem-side — the manifest read, the crawl, the digest —
/// happens before the write, so the graph write is one short transaction per device.
/// </summary>
public sealed class EngineeringGraphProjectionService
{
    private readonly EngineeringGraphService _graph;

    public EngineeringGraphProjectionService(EngineeringGraphService graph) =>
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));

    /// <summary>
    /// Ingests one device and returns what it wrote. A second call with unchanged inputs writes no
    /// row; a component the manifest no longer lists loses its node, its properties and the edges
    /// that referenced it.
    /// </summary>
    public DeviceProjectionResult ProjectDevice(DeviceContext context, DeviceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!string.Equals(context.DeviceId, metadata.DeviceId, StringComparison.Ordinal))
            throw new EngineeringGraphConstraintException("Device metadata belongs to another device.");

        var report = new List<string>();
        // The device page's diagnostics are the ones source resolution produced, exactly as the crawl
        // reader produced them; the ingest's own note about *why* the crawl ran is a report line that
        // the page never showed and must not start showing (AC-002).
        var diagnostics = new List<string>();
        var manifest = DeviceSnapshotReader.ReadManifestSourceObjects(context.SourceRoot);
        var usedCrawlFallback = manifest.Count == 0;
        IReadOnlyList<SourceObjectInfo> sources;
        if (usedCrawlFallback)
        {
            report.Add(
                "The export manifest 'metadata.json' is missing or legacy; source objects were resolved from the block crawl, and an object the crawl cannot classify is left unclassified.");
            sources = DeviceSnapshotReader.ReadCrawledSourceObjects(context, diagnostics);
        }
        else
        {
            sources = manifest;
        }

        var device = DeviceSnapshotReader.ReadDeviceExportMetadata(context);
        var digest = ManifestDigest(context, sources, device);
        var nodes = BuildNodes(context, metadata, device, sources, diagnostics, digest);
        var write = _graph.ReplaceDeviceProperties(context.DeviceId, nodes);
        var blockCount = sources.Count(item => DeviceSnapshotReader.IsBlockCategoryPath(item.RelativePath));
        return new DeviceProjectionResult(
            context.DeviceId, digest, usedCrawlFallback, sources.Count, blockCount, write, report, diagnostics);
    }

    /// <summary>The digest the projection stores and a selection boundary compares (ADR-0012): the
    /// export root plus every manifest field the projection keeps, so a change to any of them moves
    /// the digest with it.</summary>
    private static string ManifestDigest(DeviceContext context, IReadOnlyList<SourceObjectInfo> sources, DeviceExportMetadata? device)
    {
        var payload = JsonSerializer.Serialize(new
        {
            root = context.SourceRoot,
            device,
            sources = sources.Select(item => new
            {
                item.Id,
                item.Name,
                item.Number,
                item.Category,
                item.ProgrammingLanguage,
                item.GroupPath,
                item.RelativePath,
                item.ContentHash,
                item.IsKnowHowProtected,
                item.ModifiedDate,
                item.Status,
                Fingerprints = item.FingerprintComponents?.ToCanonicalString(),
                item.EvidenceKind,
            }),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static IReadOnlyList<GraphNodePropertySet> BuildNodes(
        DeviceContext context,
        DeviceMetadata metadata,
        DeviceExportMetadata? device,
        IReadOnlyList<SourceObjectInfo> sources,
        IReadOnlyList<string> diagnostics,
        string digest)
    {
        var nodes = new List<GraphNodePropertySet>(sources.Count + 1)
        {
            new(
                new GraphEntity(GraphEntityKind.Device, context.DeviceId, context.WorkbenchId, context.WorktreeId, context.DeviceId),
                DeviceProperties(context, metadata, device, sources, diagnostics, digest)),
        };
        foreach (var item in sources)
        {
            nodes.Add(new GraphNodePropertySet(
                new GraphEntity(GraphEntityKind.SourceObject, $"{context.DeviceId}:{item.Id}", context.WorkbenchId,
                    context.WorktreeId, context.DeviceId, item.RelativePath),
                SourceObjectProperties(item)));
        }

        return nodes;
    }

    private static IReadOnlyList<GraphProperty> DeviceProperties(
        DeviceContext context,
        DeviceMetadata metadata,
        DeviceExportMetadata? device,
        IReadOnlyList<SourceObjectInfo> sources,
        IReadOnlyList<string> diagnostics,
        string digest)
    {
        const string source = GraphPropertySource.DeviceProjection;
        var knowledge = DeviceSnapshotReader.ReadKnowledgeSnapshot(context, metadata);
        var properties = new List<GraphProperty>
        {
            GraphProperty.TextValue(DevicePropertyNames.WorkbenchId, source, context.WorkbenchId),
            GraphProperty.TextValue(DevicePropertyNames.WorktreeId, source, context.WorktreeId),
            GraphProperty.TextValue(DevicePropertyNames.DeviceId, source, context.DeviceId),
            GraphProperty.TextValue(DevicePropertyNames.PlcName, source, metadata.PlcName),
            GraphProperty.TextValue(DevicePropertyNames.EngineeringIdentity, source, metadata.EngineeringIdentity),
            GraphProperty.TextValue(DevicePropertyNames.SourceRoot, source, context.SourceRoot),
            GraphProperty.TextValue(DevicePropertyNames.SourceProjectPath, source, DeviceSnapshotReader.ReadSourceProjectPath(context)),
            GraphProperty.TextValue(DevicePropertyNames.KnowledgeState, source, knowledge.State),
            GraphProperty.TextValue(DevicePropertyNames.KnowledgeUpdatedAt, source, knowledge.UpdatedAt),
            GraphProperty.NumberValue(DevicePropertyNames.BlockCount, source,
                sources.Count(item => DeviceSnapshotReader.IsBlockCategoryPath(item.RelativePath))),
            GraphProperty.NumberValue(DevicePropertyNames.SourceObjectCount, source, sources.Count),
            GraphProperty.JsonValue(DevicePropertyNames.Diagnostics, source, JsonSerializer.Serialize(diagnostics)),
            GraphProperty.FlagValue(DevicePropertyNames.ProjectionInvalidated, source, false),
            GraphProperty.TextValue(DevicePropertyNames.ProjectionManifestDigest, source, digest),
        };

        // The manifest's optional "device" section is stored only when the export carries one, so a
        // reader can still tell a legacy manifest (no section) from a section with empty fields.
        if (device is not null)
        {
            properties.Add(GraphProperty.TextValue(DevicePropertyNames.ExportPlcName, source, device.PlcName));
            properties.Add(GraphProperty.TextValue(DevicePropertyNames.ExportDeviceName, source, device.DeviceName));
            properties.Add(GraphProperty.TextValue(DevicePropertyNames.ExportTypeIdentifier, source, device.TypeIdentifier));
            properties.Add(GraphProperty.TextValue(DevicePropertyNames.ExportProjectName, source, device.ProjectName));
            properties.Add(GraphProperty.TextValue(DevicePropertyNames.ExportProjectAuthor, source, device.ProjectAuthor));
            properties.Add(GraphProperty.TextValue(DevicePropertyNames.ExportProjectComment, source, device.ProjectComment));
            properties.Add(GraphProperty.TextValue(DevicePropertyNames.ExportProjectVersion, source, device.ProjectVersion));
            properties.Add(GraphProperty.TextValue(DevicePropertyNames.ExportProjectCopyright, source, device.ProjectCopyright));
            properties.Add(GraphProperty.TimestampValue(DevicePropertyNames.ExportProjectCreationTime, source, device.ProjectCreationTime));
            properties.Add(GraphProperty.TimestampValue(DevicePropertyNames.ExportProjectLastModified, source, device.ProjectLastModified));
            properties.Add(GraphProperty.TextValue(DevicePropertyNames.ExportProjectLastModifiedBy, source, device.ProjectLastModifiedBy));
            properties.Add(GraphProperty.FlagValue(DevicePropertyNames.ExportIsSafetyDevice, source, device.IsSafetyDevice));
            properties.Add(GraphProperty.TextValue(DevicePropertyNames.ExportFSignatureReadState, source, device.FSignatureReadState));
            properties.Add(GraphProperty.TextValue(DevicePropertyNames.ExportFSignature, source, device.FSignature));
        }

        return properties;
    }

    private static IReadOnlyList<GraphProperty> SourceObjectProperties(SourceObjectInfo item)
    {
        const string source = GraphPropertySource.DeviceProjection;
        return
        [
            GraphProperty.TextValue(SourceObjectPropertyNames.Id, source, item.Id),
            GraphProperty.TextValue(SourceObjectPropertyNames.Name, source, item.Name),
            GraphProperty.NumberValue(SourceObjectPropertyNames.Number, source, item.Number),
            GraphProperty.TextValue(SourceObjectPropertyNames.Category, source, item.Category),
            GraphProperty.TextValue(SourceObjectPropertyNames.ProgrammingLanguage, source, item.ProgrammingLanguage),
            GraphProperty.TextValue(SourceObjectPropertyNames.GroupPath, source, item.GroupPath),
            GraphProperty.TextValue(SourceObjectPropertyNames.RelativePath, source, item.RelativePath),
            GraphProperty.TextValue(SourceObjectPropertyNames.ContentHash, source, item.ContentHash),
            GraphProperty.FlagValue(SourceObjectPropertyNames.IsKnowHowProtected, source, item.IsKnowHowProtected),
            GraphProperty.TimestampValue(SourceObjectPropertyNames.ModifiedDate, source, item.ModifiedDate),
            GraphProperty.TextValue(SourceObjectPropertyNames.Status, source, item.Status),
            GraphProperty.JsonValue(SourceObjectPropertyNames.Fingerprints, source, FingerprintJson(item.FingerprintComponents)),
            GraphProperty.TextValue(SourceObjectPropertyNames.EvidenceKind, source, item.EvidenceKind),
            GraphProperty.FlagValue(SourceObjectPropertyNames.IsBlock, source, DeviceSnapshotReader.IsBlockCategoryPath(item.RelativePath)),
            // The value the device page shows today: the block crawl has always produced false, and
            // AC-002 preserves it. The manifest's status and modifiedDate are stored alongside it.
            GraphProperty.FlagValue(SourceObjectPropertyNames.Modified, source, false),
        ];
    }

    /// <summary>Fingerprints as a JSON object with sorted names, so the same set always serialises to
    /// the same text and never looks changed to the next ingest.</summary>
    private static string? FingerprintJson(FingerprintSet? fingerprints) =>
        fingerprints is null || fingerprints.Count == 0
            ? null
            : JsonSerializer.Serialize(fingerprints
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}
