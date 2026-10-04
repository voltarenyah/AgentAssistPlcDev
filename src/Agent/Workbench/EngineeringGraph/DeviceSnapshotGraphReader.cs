using System.Text.Json;
using Contracts.Engineering;

namespace Agent.Workbench.EngineeringGraph;

/// <summary>Which input moved the selection boundary into re-projecting a device (AC-004's
/// "the boundary records which input moved").</summary>
public enum ProjectionBoundaryReason
{
    /// <summary>The stored projection already reflects the manifest on disk.</summary>
    None,

    /// <summary>The graph holds no projection for the device.</summary>
    Missing,

    /// <summary>A fact the caller already holds moved: workbench, worktree, device, PLC name,
    /// engineering identity, source root, or the knowledge state and its timestamp.</summary>
    Identity,

    /// <summary>A write point flagged the projection as invalidated (ADR-0012 item 2).</summary>
    Invalidated,

    /// <summary>The stored manifest digest differs from the manifest on disk (ADR-0012 item 3).</summary>
    Digest,

    /// <summary>The export manifest is missing or legacy, so it carries no comparable digest. The
    /// stored facts are served; the write points are what refresh such a device, because crawling on
    /// every read would put the legacy path far outside AC-001's budget.</summary>
    LegacyManifest,
}

/// <summary>What one selection/refresh boundary check observed and, on a mismatch, re-projected.</summary>
public sealed record ProjectionBoundaryResult(
    ProjectionBoundaryReason Reason,
    DeviceProjectionResult? Projection)
{
    public bool Reprojected => Projection is not null;
}

/// <summary>
/// The device read path (ADR-0011): the device page's facts are assembled from the engineering graph
/// instead of walking the exported source tree. A device whose projection is missing — a fresh or
/// upgraded database — is projected on demand before it is served, so no read parses block XML or the
/// export manifest. <see cref="DeviceSnapshotReader"/> stays the ingest implementation and the fallback
/// for a missing or legacy manifest.
/// </summary>
/// <remarks>
/// The caller owns the graph scope: one scope per request, never one per row or per device fact. The
/// response shapes are the ones <see cref="DeviceSnapshotReader.Read"/> produced, field for field, so
/// the Studio client is unchanged.
/// </remarks>
public sealed class DeviceSnapshotGraphReader
{
    private readonly EngineeringGraphService _graph;
    private readonly EngineeringGraphProjectionService _projection;

    public DeviceSnapshotGraphReader(EngineeringGraphService graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _projection = new EngineeringGraphProjectionService(_graph);
    }

    /// <summary>The device snapshot exactly as the page reads it today, assembled from the graph.</summary>
    public DeviceSnapshot Read(DeviceContext context, DeviceMetadata metadata)
    {
        var facts = Facts(context, metadata);
        var device = Lookup(facts, context.DeviceId);
        var sources = ProjectedSources(facts);
        var blocks = sources.Where(IsBlock).Select(ToBlock).ToArray();
        return new DeviceSnapshot(
            PropertyText(device, DevicePropertyNames.WorkbenchId) ?? context.WorkbenchId,
            PropertyText(device, DevicePropertyNames.WorktreeId) ?? context.WorktreeId,
            PropertyText(device, DevicePropertyNames.DeviceId) ?? context.DeviceId,
            PropertyText(device, DevicePropertyNames.PlcName) ?? metadata.PlcName,
            PropertyText(device, DevicePropertyNames.EngineeringIdentity) ?? metadata.EngineeringIdentity,
            PropertyText(device, DevicePropertyNames.SourceRoot) ?? context.SourceRoot,
            context.KnowledgeDbPath,
            PropertyText(device, DevicePropertyNames.SourceProjectPath),
            new DeviceKnowledgeSnapshot(
                PropertyText(device, DevicePropertyNames.KnowledgeState) ?? "missing",
                PropertyText(device, DevicePropertyNames.KnowledgeUpdatedAt)),
            blocks,
            sources.Select(source => source.Info).ToArray(),
            // The device page's count has always been the block-category count (`blocks.Count`), not the
            // number of manifest objects; the picker list is the larger number and stays larger.
            blocks.Length,
            Diagnostics(device),
            ExportMetadata(device));
    }

    /// <summary>The block-category subset, in the crawl's own order and with its own values.</summary>
    public IReadOnlyList<OfflineBlockInfo> ReadBlocks(DeviceContext context, DeviceMetadata metadata) =>
        ProjectedSources(Facts(context, metadata)).Where(IsBlock).Select(ToBlock).ToArray();

    /// <summary>The source objects a device exposes, ordered as the manifest reader ordered them.</summary>
    /// <param name="comparableOnly">Also drop the kinds the managed-source evidence domain excludes, as
    /// <see cref="DeviceSnapshotReader.ReadSourceObjects"/> does for the task picker.</param>
    public IReadOnlyList<SourceObjectInfo> ReadSourceObjects(
        DeviceContext context,
        DeviceMetadata metadata,
        bool comparableOnly = false)
    {
        var sources = ProjectedSources(Facts(context, metadata)).Select(source => source.Info).ToArray();
        return comparableOnly ? DeviceSnapshotReader.ComparableSourceObjects(sources) : sources;
    }

    /// <summary>
    /// The anchor a source object's traceability read needs: true when the device's projection lists
    /// the object, with the relative path the projection stored. The caller keeps the existing rule
    /// that only a device of the current selection is consulted.
    /// </summary>
    public bool TryGetListedSourceObject(
        DeviceContext context,
        DeviceMetadata metadata,
        string manifestId,
        out string relativePath)
    {
        relativePath = string.Empty;
        var properties = Lookup(Facts(context, metadata), $"{context.DeviceId}:{manifestId}");
        var relative = PropertyText(properties, SourceObjectPropertyNames.RelativePath);
        if (string.IsNullOrEmpty(relative)) return false;
        relativePath = relative;
        return true;
    }

    /// <summary>
    /// The same anchor, addressed by the reader's own block id form — <c>source:{relativePath}</c>,
    /// which <see cref="DeviceSnapshotReader.ReadManifestSourceObjects"/> falls back to for a legacy
    /// manifest component without an id and which the block view's <c>OfflineBlockInfo.Id</c> has
    /// always been. Returns the canonical <c>{deviceId}:{manifestId}</c> entity id the graph stores,
    /// with the relative path the projection kept, or null when the device does not list the path.
    /// </summary>
    public (string EntityId, string RelativePath)? FindListedSourceObjectByPath(
        DeviceContext context,
        DeviceMetadata metadata,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metadata);
        var wanted = NormalizePath(relativePath);
        foreach (var pair in Facts(context, metadata))
        {
            var stored = PropertyText(pair.Value, SourceObjectPropertyNames.RelativePath);
            if (stored is null) continue;
            if (!string.Equals(NormalizePath(stored), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            return (pair.Key, stored);
        }

        return null;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// The selection/refresh boundary (ADR-0012 item 3, AC-004): a projection already flagged as
    /// invalidated, or one whose stored manifest digest — the export root plus every projected manifest
    /// field, not <c>contentHash</c> alone — no longer matches the manifest on disk, is re-projected
    /// before the caller serves. It shares the caller's graph scope with the rows it guards, and only
    /// facts the caller already has in hand are compared when no export read is needed.
    /// </summary>
    public ProjectionBoundaryResult EnsureProjectionCurrent(DeviceContext context, DeviceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metadata);
        var facts = _graph.GetDeviceProperties(context.DeviceId);
        var reason = MovedInput(facts, context, metadata, out var input);
        return reason is ProjectionBoundaryReason.None or ProjectionBoundaryReason.LegacyManifest
            ? new ProjectionBoundaryResult(reason, null)
            : new ProjectionBoundaryResult(reason, Project(context, metadata, input));
    }

    /// <summary>
    /// One device's fact rows, re-projecting first when the boundary found the stored projection
    /// missing, invalidated, or behind the manifest on disk.
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyList<GraphProperty>> Facts(DeviceContext context, DeviceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metadata);
        var facts = _graph.GetDeviceProperties(context.DeviceId);
        var reason = MovedInput(facts, context, metadata, out var input);
        if (reason is ProjectionBoundaryReason.None or ProjectionBoundaryReason.LegacyManifest) return facts;
        Project(context, metadata, input);
        return _graph.GetDeviceProperties(context.DeviceId);
    }

    /// <summary>
    /// A projection that fails at the boundary is reported as a projection failure — never as a read
    /// failure and never by serving the stale facts it was meant to replace (ADR-0012, Negative
    /// Consequences). <see cref="EngineeringGraphProjectionService.ProjectDevice"/> raises that error;
    /// a rejected input path stays the path error it already was.
    /// </summary>
    private DeviceProjectionResult Project(
        DeviceContext context,
        DeviceMetadata metadata,
        DeviceProjectionInput? input) =>
        _projection.ProjectDevice(context, metadata, input);

    /// <summary>
    /// Which input moved, or <see cref="ProjectionBoundaryReason.None"/>. When the answer is
    /// <see cref="ProjectionBoundaryReason.Digest"/>, <paramref name="input"/> carries the manifest
    /// parse the mismatch already cost, so the re-projection never reads it again.
    /// </summary>
    private static ProjectionBoundaryReason MovedInput(
        IReadOnlyDictionary<string, IReadOnlyList<GraphProperty>> facts,
        DeviceContext context,
        DeviceMetadata metadata,
        out DeviceProjectionInput? input)
    {
        input = null;
        if (!facts.TryGetValue(context.DeviceId, out var stored) || stored.Count == 0)
            return ProjectionBoundaryReason.Missing;
        if (!IdentityMatches(stored, context, metadata))
            return ProjectionBoundaryReason.Identity;
        if (PropertyFlag(stored, DevicePropertyNames.ProjectionInvalidated) == true)
            return ProjectionBoundaryReason.Invalidated;
        var digest = PropertyText(stored, DevicePropertyNames.ProjectionManifestDigest);
        if (string.IsNullOrEmpty(digest))
            return ProjectionBoundaryReason.Missing;

        // The boundary's one export read: the same single metadata.json parse the ingest performs.
        var resolved = EngineeringGraphProjectionService.ReadInput(context);
        if (resolved.UsedCrawlFallback)
            return ProjectionBoundaryReason.LegacyManifest;
        if (string.Equals(EngineeringGraphProjectionService.ComputeDigest(context, resolved), digest, StringComparison.Ordinal))
            return ProjectionBoundaryReason.None;
        input = resolved;
        return ProjectionBoundaryReason.Digest;
    }

    /// <summary>The facts the caller already holds, plus the knowledge state — which follows the
    /// knowledge database's existence and the persisted staleness flags, never the export.</summary>
    private static bool IdentityMatches(
        IReadOnlyList<GraphProperty> stored,
        DeviceContext context,
        DeviceMetadata metadata)
    {
        if (PropertyText(stored, DevicePropertyNames.WorkbenchId) != context.WorkbenchId) return false;
        if (PropertyText(stored, DevicePropertyNames.WorktreeId) != context.WorktreeId) return false;
        if (PropertyText(stored, DevicePropertyNames.DeviceId) != context.DeviceId) return false;
        if (PropertyText(stored, DevicePropertyNames.PlcName) != metadata.PlcName) return false;
        if (PropertyText(stored, DevicePropertyNames.EngineeringIdentity) != metadata.EngineeringIdentity) return false;
        if (PropertyText(stored, DevicePropertyNames.SourceRoot) != context.SourceRoot) return false;
        var knowledge = DeviceSnapshotReader.ReadKnowledgeSnapshot(context, metadata);
        if (PropertyText(stored, DevicePropertyNames.KnowledgeState) != knowledge.State) return false;
        return PropertyText(stored, DevicePropertyNames.KnowledgeUpdatedAt) == knowledge.UpdatedAt;
    }

    /// <summary>One projected source object with its property rows, so the block view can read the
    /// stored block flag instead of re-deriving it.</summary>
    private sealed record ProjectedSource(IReadOnlyList<GraphProperty> Properties, SourceObjectInfo Info);

    private static IReadOnlyList<GraphProperty> Lookup(
        IReadOnlyDictionary<string, IReadOnlyList<GraphProperty>> facts,
        string entityId) =>
        facts.TryGetValue(entityId, out var properties) ? properties : [];

    /// <summary>The device's source objects, ordered by category, number, name and path — the manifest
    /// reader's own order, which is also the block crawl's order (a block's category is its block
    /// type).</summary>
    private static ProjectedSource[] ProjectedSources(
        IReadOnlyDictionary<string, IReadOnlyList<GraphProperty>> facts) =>
        facts
            .Where(pair => pair.Value.Any(property => property.Name == SourceObjectPropertyNames.RelativePath))
            .Select(pair => new ProjectedSource(pair.Value, ToSourceObject(pair.Value)))
            .OrderBy(source => source.Info.Category, StringComparer.Ordinal)
            .ThenBy(source => source.Info.Number ?? int.MaxValue)
            .ThenBy(source => source.Info.Name, StringComparer.Ordinal)
            .ThenBy(source => source.Info.RelativePath, StringComparer.Ordinal)
            .ToArray();

    private static SourceObjectInfo ToSourceObject(IReadOnlyList<GraphProperty> properties) => new(
        PropertyText(properties, SourceObjectPropertyNames.Id) ?? string.Empty,
        PropertyText(properties, SourceObjectPropertyNames.Name) ?? string.Empty,
        PropertyNumber(properties, SourceObjectPropertyNames.Number),
        PropertyText(properties, SourceObjectPropertyNames.Category) ?? string.Empty,
        PropertyText(properties, SourceObjectPropertyNames.ProgrammingLanguage),
        PropertyText(properties, SourceObjectPropertyNames.GroupPath),
        PropertyText(properties, SourceObjectPropertyNames.RelativePath) ?? string.Empty,
        PropertyText(properties, SourceObjectPropertyNames.ContentHash),
        PropertyFlag(properties, SourceObjectPropertyNames.IsKnowHowProtected),
        PropertyTimestamp(properties, SourceObjectPropertyNames.ModifiedDate),
        PropertyText(properties, SourceObjectPropertyNames.Status),
        Fingerprints(properties),
        PropertyText(properties, SourceObjectPropertyNames.EvidenceKind));

    private static bool IsBlock(ProjectedSource source) =>
        PropertyFlag(source.Properties, SourceObjectPropertyNames.IsBlock)
        ?? DeviceSnapshotReader.IsBlockCategoryPath(source.Info.RelativePath);

    /// <summary>A block as the page has always shown it: the crawl's own `source:{relativePath}` id and
    /// its `modified` flag (today always false — the stored flag, which the projection keeps at the
    /// crawl's value), with the object's name, number, type and language. The block type is the
    /// manifest category, which is the crawl's block type for every block-category path (`Blocks/` and
    /// `DB/` are the categories OB/FB/FC/DB).</summary>
    private static OfflineBlockInfo ToBlock(ProjectedSource source) => new(
        $"source:{source.Info.RelativePath}",
        source.Info.Name,
        source.Info.Number,
        source.Info.Category,
        source.Info.ProgrammingLanguage,
        source.Info.GroupPath,
        source.Info.RelativePath,
        PropertyFlag(source.Properties, SourceObjectPropertyNames.Modified) ?? false);

    private static FingerprintSet? Fingerprints(IReadOnlyList<GraphProperty> properties)
    {
        var json = PropertyJson(properties, SourceObjectPropertyNames.Fingerprints);
        if (string.IsNullOrEmpty(json)) return null;
        Dictionary<string, string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null || parsed.Count == 0) return null;
        var fingerprints = new FingerprintSet();
        foreach (var pair in parsed) fingerprints[pair.Key] = pair.Value;
        return fingerprints;
    }

    private static IReadOnlyList<string> Diagnostics(IReadOnlyList<GraphProperty> device)
    {
        var json = PropertyJson(device, DevicePropertyNames.Diagnostics);
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The manifest's optional "device" section, or null when the export carried none — the
    /// projection stores its 14 fields as `export.*` properties exactly then.</summary>
    private static DeviceExportMetadata? ExportMetadata(IReadOnlyList<GraphProperty> device)
    {
        if (!device.Any(property => property.Name.StartsWith("export.", StringComparison.Ordinal))) return null;
        return new DeviceExportMetadata(
            PropertyText(device, DevicePropertyNames.ExportPlcName),
            PropertyText(device, DevicePropertyNames.ExportDeviceName),
            PropertyText(device, DevicePropertyNames.ExportTypeIdentifier),
            PropertyText(device, DevicePropertyNames.ExportProjectName),
            PropertyText(device, DevicePropertyNames.ExportProjectAuthor),
            PropertyText(device, DevicePropertyNames.ExportProjectComment),
            PropertyText(device, DevicePropertyNames.ExportProjectVersion),
            PropertyText(device, DevicePropertyNames.ExportProjectCopyright),
            PropertyTimestamp(device, DevicePropertyNames.ExportProjectCreationTime),
            PropertyTimestamp(device, DevicePropertyNames.ExportProjectLastModified),
            PropertyText(device, DevicePropertyNames.ExportProjectLastModifiedBy),
            PropertyFlag(device, DevicePropertyNames.ExportIsSafetyDevice),
            PropertyText(device, DevicePropertyNames.ExportFSignatureReadState),
            PropertyText(device, DevicePropertyNames.ExportFSignature));
    }

    private static GraphProperty? Property(IReadOnlyList<GraphProperty> properties, string name) =>
        properties.FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal));

    private static string? PropertyText(IReadOnlyList<GraphProperty> properties, string name) =>
        Property(properties, name)?.Text;

    private static string? PropertyJson(IReadOnlyList<GraphProperty> properties, string name) =>
        Property(properties, name)?.Json;

    private static bool? PropertyFlag(IReadOnlyList<GraphProperty> properties, string name) =>
        Property(properties, name)?.Flag;

    private static DateTimeOffset? PropertyTimestamp(IReadOnlyList<GraphProperty> properties, string name) =>
        Property(properties, name)?.Timestamp;

    private static int? PropertyNumber(IReadOnlyList<GraphProperty> properties, string name)
    {
        var number = Property(properties, name)?.Number;
        return number is null ? null : (int)Math.Round(number.Value, MidpointRounding.AwayFromZero);
    }
}
