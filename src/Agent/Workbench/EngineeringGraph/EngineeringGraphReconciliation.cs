namespace Agent.Workbench.EngineeringGraph;

/// <summary>
/// The reconciliation pass ADR-0012 item 4 asks for: it re-projects a device whose projection an
/// <em>event</em> invalidated but whose manifest digest still matches, and it removes the facts of
/// worktrees that no longer exist. It is the answer to the two silent changes the write points and the
/// selection boundary between them cannot catch: a flag whose read never followed, and a worktree that
/// was removed while its rows stayed in the shared per-workbench database.
/// </summary>
public sealed class EngineeringGraphReconciliation
{
    private readonly EngineeringGraphService _graph;
    private readonly EngineeringGraphProjectionService _projection;

    public EngineeringGraphReconciliation(EngineeringGraphService graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _projection = new EngineeringGraphProjectionService(_graph);
    }

    /// <summary>
    /// Re-projects one device whose projection a write point flagged (ADR-0012 item 2), and reports which
    /// input moved: the invalidation flag, and whether the manifest digest still agreed when it did
    /// (AC-004). A device nothing flagged is left alone and costs one property read — no export read.
    /// </summary>
    public DeviceProjectionRepairResult RepairDevice(DeviceContext context, DeviceMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(metadata);
        var stored = _graph.GetProperties(GraphEntityKind.Device, context.DeviceId);
        var invalidated = stored
            .FirstOrDefault(property => string.Equals(
                property.Name, DevicePropertyNames.ProjectionInvalidated, StringComparison.Ordinal))
            ?.Flag == true;
        if (!invalidated) return new DeviceProjectionRepairResult(context.DeviceId, false, false, null);

        var input = EngineeringGraphProjectionService.ReadInput(context);
        var storedDigest = stored
            .FirstOrDefault(property => string.Equals(
                property.Name, DevicePropertyNames.ProjectionManifestDigest, StringComparison.Ordinal))
            ?.Text;
        var digestMatched = string.Equals(
            EngineeringGraphProjectionService.ComputeDigest(context, input), storedDigest, StringComparison.Ordinal);
        var projection = _projection.ProjectDevice(context, metadata, input);
        return new DeviceProjectionRepairResult(context.DeviceId, true, digestMatched, projection);
    }

    /// <summary>
    /// Removes the facts of every worktree the current Workbench no longer registers (AC-007): its nodes,
    /// their property rows and every edge that references them. A device id another worktree still
    /// registers keeps its facts.
    /// </summary>
    public WorktreeFactCleanupResult RemoveDeletedWorktreeFacts() =>
        _graph.RemoveUnregisteredWorktreeFacts();
}
