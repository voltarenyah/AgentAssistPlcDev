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
/// Everything one device projection reads from the filesystem, resolved before any graph write so the
/// write is one short transaction (ADR-0012 item 9). The selection boundary resolves it too, so a
/// digest mismatch costs the same single manifest parse the projection would have done and the
/// re-projection reuses it instead of reading the manifest twice.
/// </summary>
public sealed record DeviceProjectionInput(
    IReadOnlyList<SourceObjectInfo> Sources,
    DeviceExportMetadata? Device,
    bool UsedCrawlFallback,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> Report);

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
    public DeviceProjectionResult ProjectDevice(DeviceContext context, DeviceMetadata metadata) =>
        ProjectDevice(context, metadata, input: null);

    /// <summary>
    /// The same ingest with an already-resolved <paramref name="input"/>, so a caller that had to read
    /// the manifest to compare digests does not read it a second time. A projection that fails is
    /// reported as a projection failure (AC-004), never left to look like the read that followed it; a
    /// rejected input path or argument keeps the error it already was.
    /// </summary>
    public DeviceProjectionResult ProjectDevice(DeviceContext context, DeviceMetadata metadata, DeviceProjectionInput? input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!string.Equals(context.DeviceId, metadata.DeviceId, StringComparison.Ordinal))
            throw new EngineeringGraphConstraintException("Device metadata belongs to another device.");

        try
        {
            // The device's owning worktrees are read before the transaction and written inside it, so a
            // device id another worktree also registers never loses that ownership (AC-007).
            var owners = ProjectionWorktrees(context);
            var resolved = input ?? ReadInput(context);
            var digest = ComputeDigest(context, resolved);
            var nodes = BuildNodes(context, metadata, resolved, owners, digest);
            var write = _graph.ReplaceDeviceProperties(context.DeviceId, nodes);
            var blockCount = resolved.Sources.Count(item => DeviceSnapshotReader.IsBlockCategoryPath(item.RelativePath));
            return new DeviceProjectionResult(
                context.DeviceId, digest, resolved.UsedCrawlFallback, resolved.Sources.Count, blockCount, write,
                resolved.Report, resolved.Diagnostics);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            and not ArgumentException
            and not WorkbenchPathException
            and not EngineeringGraphProjectionException)
        {
            throw new EngineeringGraphProjectionException(
                $"The projection of device '{context.DeviceId}' failed: {exception.Message}",
                exception);
        }
    }

    /// <summary>
    /// Resolves everything the projection reads from the filesystem for one device: the manifest (or,
    /// when it is missing or legacy, the block crawl), the export's device section and the ingest's own
    /// report. No graph write happens here.
    /// </summary>
    public static DeviceProjectionInput ReadInput(DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var report = new List<string>();
        // The device page's diagnostics are the ones source resolution produced, exactly as the crawl
        // reader produced them; the ingest's own note about *why* the crawl ran is a report line that
        // the page never showed and must not start showing (AC-002).
        var diagnostics = new List<string>();
        var sections = DeviceSnapshotReader.ReadManifestSections(context.SourceRoot);
        var usedCrawlFallback = sections.Sources.Count == 0;
        IReadOnlyList<SourceObjectInfo> sources;
        if (usedCrawlFallback)
        {
            report.Add(
                "The export manifest 'metadata.json' is missing or legacy; source objects were resolved from the block crawl, and an object the crawl cannot classify is left unclassified.");
            sources = DeviceSnapshotReader.ReadCrawledSourceObjects(context, diagnostics);
        }
        else
        {
            sources = sections.Sources;
        }

        return new DeviceProjectionInput(sources, sections.Device, usedCrawlFallback, diagnostics, report);
    }

    /// <summary>The digest the projection stores and a selection boundary compares (ADR-0012): the
    /// export root plus every manifest field the projection keeps, so a change to any of them moves
    /// the digest with it. Computed from the already-resolved projection input.</summary>
    public static string ComputeDigest(DeviceContext context, DeviceProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var payload = JsonSerializer.Serialize(new
        {
            root = context.SourceRoot,
            device = input.Device,
            sources = input.Sources.Select(item => new
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

    /// <summary>
    /// Every worktree that registers this device, with <paramref name="context"/>'s worktree added:
    /// the stored set plus this ingest's owner. Read before the projection's transaction, because
    /// <c>graph_entities.worktree_id</c> alone would be last-writer-wins for a device id registered in
    /// more than one worktree (AC-007).
    /// </summary>
    private IReadOnlyList<string> ProjectionWorktrees(DeviceContext context)
    {
        var owners = new SortedSet<string>(StringComparer.Ordinal);
        var stored = _graph.GetProperties(GraphEntityKind.Device, context.DeviceId)
            .FirstOrDefault(property => string.Equals(
                property.Name, DevicePropertyNames.ProjectionWorktrees, StringComparison.Ordinal));
        foreach (var worktreeId in ParseWorktrees(stored?.Json)) owners.Add(worktreeId);
        owners.Add(context.WorktreeId);
        return owners.ToArray();
    }

    /// <summary>The worktree ids a stored <c>projection.worktrees</c> property names, tolerating a
    /// missing or unreadable value (a database migrated from an earlier schema).</summary>
    internal static IReadOnlyList<string> ParseWorktrees(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json)?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The worktree a projected node's entity row is attributed to: the ordinal-first owner,
    /// so the row is stable whichever worktree projected last.</summary>
    internal static string PrimaryWorktree(IReadOnlyList<string> owners, string fallback) =>
        owners.Count == 0 ? fallback : owners[0];

    private static IReadOnlyList<GraphNodePropertySet> BuildNodes(
        DeviceContext context,
        DeviceMetadata metadata,
        DeviceProjectionInput input,
        IReadOnlyList<string> owners,
        string digest)
    {
        var owner = PrimaryWorktree(owners, context.WorktreeId);
        var sources = input.Sources;
        var nodes = new List<GraphNodePropertySet>(sources.Count + 1)
        {
            new(
                new GraphEntity(GraphEntityKind.Device, context.DeviceId, context.WorkbenchId, owner, context.DeviceId),
                DeviceProperties(context, metadata, input, owners, digest)),
        };
        foreach (var item in sources)
        {
            nodes.Add(new GraphNodePropertySet(
                new GraphEntity(GraphEntityKind.SourceObject, $"{context.DeviceId}:{item.Id}", context.WorkbenchId,
                    owner, context.DeviceId, item.RelativePath),
                SourceObjectProperties(item)));
        }

        return nodes;
    }

    private static IReadOnlyList<GraphProperty> DeviceProperties(
        DeviceContext context,
        DeviceMetadata metadata,
        DeviceProjectionInput input,
        IReadOnlyList<string> owners,
        string digest)
    {
        const string source = GraphPropertySource.DeviceProjection;
        var device = input.Device;
        var sources = input.Sources;
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
            GraphProperty.JsonValue(DevicePropertyNames.Diagnostics, source, JsonSerializer.Serialize(input.Diagnostics)),
            GraphProperty.FlagValue(DevicePropertyNames.ProjectionInvalidated, source, false),
            GraphProperty.TextValue(DevicePropertyNames.ProjectionManifestDigest, source, digest),
            GraphProperty.JsonValue(DevicePropertyNames.ProjectionWorktrees, source,
                JsonSerializer.Serialize(owners)),
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
