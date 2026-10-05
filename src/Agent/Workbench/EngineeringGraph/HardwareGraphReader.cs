using System.Text.Json;

namespace Agent.Workbench.EngineeringGraph;

/// <summary>Which input moved the hardware boundary into re-projecting a worktree's subtree.</summary>
public enum HardwareBoundaryReason
{
    /// <summary>The stored projection already reflects the hardware subtree on disk.</summary>
    None,

    /// <summary>The graph holds no hardware projection for the worktree.</summary>
    Missing,

    /// <summary>The stored projection was written from an older view format.</summary>
    Outdated,

    /// <summary>A write point flagged the projection as invalidated.</summary>
    Invalidated,

    /// <summary>The stored digest differs from the hardware subtree on disk.</summary>
    Digest,
}

/// <summary>
/// The hardware routes' read path (ADR-0011 Phase 5): the configuration, bill-of-materials and network
/// views are assembled from the engineering graph instead of parsing <c>project.aml</c> per request. A
/// worktree whose hardware subtree was never projected — or whose stored digest no longer matches the
/// files — is projected on demand before it is served, exactly as the device routes are.
/// </summary>
/// <remarks>
/// The caller owns the graph scope: one scope per request. The response shapes are the readers' own
/// records, deserialized from the payload the ingest stored, so the hardware pages are unchanged.
/// </remarks>
public sealed class HardwareGraphReader
{
    private readonly EngineeringGraphService _graph;
    private readonly HardwareProjectionService _projection;

    public HardwareGraphReader(EngineeringGraphService graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _projection = new HardwareProjectionService(_graph);
    }

    /// <summary>The saved hardware configuration, as <c>HardwareConfigurationReader.Read</c> returns it.</summary>
    public HardwareConfigurationView ReadConfiguration(string worktreeRoot, string worktreeId) =>
        View<HardwareConfigurationView>(worktreeRoot, worktreeId, HardwarePropertyNames.Configuration);

    /// <summary>The bill of materials, as <c>HardwareListReader.ReadBom</c> returns it.</summary>
    public HardwareBomView ReadBom(string worktreeRoot, string worktreeId) =>
        View<HardwareBomView>(worktreeRoot, worktreeId, HardwarePropertyNames.Bom);

    /// <summary>The network nodes, as <c>HardwareListReader.ReadNetwork</c> returns it.</summary>
    public HardwareNetworkView ReadNetwork(string worktreeRoot, string worktreeId) =>
        View<HardwareNetworkView>(worktreeRoot, worktreeId, HardwarePropertyNames.Network);

    /// <summary>
    /// The boundary check alone (AC-004's shape, for the hardware subtree): re-projects when the stored
    /// facts are missing, were flagged by a write point, or no longer match the subtree on disk, and
    /// reports which input moved.
    /// </summary>
    public HardwareBoundaryReason EnsureCurrent(string worktreeRoot, string worktreeId)
    {
        var stored = _graph.GetProperties(GraphEntityKind.Worktree, worktreeId);
        var reason = MovedInput(stored, worktreeRoot, out var input);
        if (reason is not HardwareBoundaryReason.None)
            _projection.ProjectHardware(worktreeRoot, worktreeId, input);
        return reason;
    }

    private T View<T>(string worktreeRoot, string worktreeId, string name)
    {
        var property = Facts(worktreeRoot, worktreeId)
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        if (property?.Json is null)
            throw new EngineeringGraphProjectionException(
                $"The hardware projection of worktree '{worktreeId}' holds no '{name}' fact.");
        try
        {
            return JsonSerializer.Deserialize<T>(property.Json, HardwareProjectionService.Json)
                ?? throw new EngineeringGraphProjectionException(
                    $"The hardware projection of worktree '{worktreeId}' holds no '{name}' fact.");
        }
        catch (JsonException exception)
        {
            throw new EngineeringGraphProjectionException(
                $"The hardware projection of worktree '{worktreeId}' could not be read: {exception.Message}",
                exception);
        }
    }

    /// <summary>The worktree's hardware facts, re-projecting first when the boundary found them
    /// missing, flagged, outdated or behind the subtree on disk.</summary>
    private IReadOnlyList<GraphProperty> Facts(string worktreeRoot, string worktreeId)
    {
        var stored = _graph.GetProperties(GraphEntityKind.Worktree, worktreeId);
        var reason = MovedInput(stored, worktreeRoot, out var input);
        if (reason is HardwareBoundaryReason.None) return stored;
        _projection.ProjectHardware(worktreeRoot, worktreeId, input);
        return _graph.GetProperties(GraphEntityKind.Worktree, worktreeId);
    }

    /// <summary>
    /// Which input moved. A flagged or outdated projection needs no file read at all; only a projection
    /// whose stamp may have moved costs the digest, which reads no XML and parses no manifest content
    /// beyond the layout resolution.
    /// </summary>
    private static HardwareBoundaryReason MovedInput(
        IReadOnlyList<GraphProperty> stored,
        string worktreeRoot,
        out HardwareProjectionInput? input)
    {
        input = null;
        if (stored.Count == 0) return HardwareBoundaryReason.Missing;
        if (!string.Equals(
                PropertyText(stored, HardwarePropertyNames.Format),
                HardwareProjectionService.CurrentFormat,
                StringComparison.Ordinal))
            return HardwareBoundaryReason.Outdated;
        if (PropertyFlag(stored, HardwarePropertyNames.Invalidated) == true)
            return HardwareBoundaryReason.Invalidated;
        var digest = PropertyText(stored, HardwarePropertyNames.Digest);
        if (string.IsNullOrEmpty(digest)) return HardwareBoundaryReason.Missing;
        if (string.Equals(HardwareDigest.Compute(worktreeRoot), digest, StringComparison.Ordinal))
            return HardwareBoundaryReason.None;
        return HardwareBoundaryReason.Digest;
    }

    private static GraphProperty? Property(IReadOnlyList<GraphProperty> properties, string name) =>
        properties.FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal));

    private static string? PropertyText(IReadOnlyList<GraphProperty> properties, string name) =>
        Property(properties, name)?.Text;

    private static bool? PropertyFlag(IReadOnlyList<GraphProperty> properties, string name) =>
        Property(properties, name)?.Flag;
}
