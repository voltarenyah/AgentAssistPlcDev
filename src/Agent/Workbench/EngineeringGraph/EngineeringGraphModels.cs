namespace Agent.Workbench.EngineeringGraph;

public enum GraphEntityKind { Task, Session, GitCommit, SourceObject, SvnRevision, Device, Worktree }
public enum GraphTaskScopeKind { Project, Worktree }
public enum GraphTaskTargetKind { Device, Hardware }
public enum GraphTaskType { Issue, Improvement, Feature }
public enum GraphTaskStatus { Todo, InProgress, Done }
public enum GraphRelationKind { TaskSession, TaskCommit, TaskSourceObject, TaskSvnRevision, CommitSourceObject, CommitSvnRevision }
public enum GraphProvenance { Manual, Default, Evidence }

public sealed record GraphTask(
    string TaskId, string WorkbenchId, GraphTaskScopeKind ScopeKind, string? WorktreeId,
    string Title, GraphTaskType Type, GraphTaskStatus Status = GraphTaskStatus.Todo,
    string? Description = null, string? MetadataJson = null,
    DateTimeOffset? CreatedUtc = null, DateTimeOffset? UpdatedUtc = null,
    int Priority = 0, string Intent = "", string ExpectedResult = "", string? DeviceId = null,
    GraphTaskTargetKind TargetKind = GraphTaskTargetKind.Device);

public sealed record TaskSourceStage(
    string TaskId, string WorktreeId, string SourceObjectId, string DeviceId, string? BaselineEvidenceJson,
    DateTimeOffset StagedUtc, DateTimeOffset? ReleasedUtc = null);

/// <summary>One active stage as seen from the worktree: the owning task's identity travels with the
/// stage so a picker can show who owns a source object before taking it over.</summary>
public sealed record WorktreeSourceStage(TaskSourceStage Stage, string TaskTitle);

public sealed record LegacyImportDiagnostic(string TaskId, string WorktreeId, string Message);
public sealed record LegacyImportResult(int ImportedCount, IReadOnlyList<LegacyImportDiagnostic> Diagnostics);

public sealed record GraphEntity(
    GraphEntityKind Kind, string EntityId, string WorkbenchId, string? WorktreeId,
    string? DeviceId = null, string? ExternalRef = null);

public sealed record GraphEdge(
    string EdgeId, GraphEntityKind FromKind, string FromId, GraphEntityKind ToKind, string ToId,
    GraphRelationKind RelationKind, GraphProvenance Provenance, bool IsPrimary,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);
public sealed record GraphFileEvidence(string CommitSha, string RelativePath, DateTimeOffset RecordedUtc);

/// <summary>Which typed column of <c>graph_entity_properties</c> carries a property's value. The kind
/// is stored alongside the value because a null value (a manifest fact with no value, such as an
/// absent safety signature) must still compare equal to itself on the next ingest — inferring the
/// kind from the populated column would read such a row back as text and rewrite it every time.</summary>
public enum GraphPropertyValueKind { Text, Number, Flag, Timestamp, Json }

/// <summary>
/// One fact attached to one node: the property table's row, keyed by (entity kind, entity id, name).
/// <paramref name="Source"/> records which ingest wrote it. Exactly one value holds the fact; a
/// property whose value is unknown carries its kind with a null value.
/// </summary>
public sealed record GraphProperty(
    string Name,
    GraphPropertyValueKind Kind,
    string Source,
    string? Text = null,
    double? Number = null,
    bool? Flag = null,
    DateTimeOffset? Timestamp = null,
    string? Json = null)
{
    public static GraphProperty TextValue(string name, string source, string? value) =>
        new(name, GraphPropertyValueKind.Text, source, Text: value);

    public static GraphProperty NumberValue(string name, string source, double? value) =>
        new(name, GraphPropertyValueKind.Number, source, Number: value);

    public static GraphProperty FlagValue(string name, string source, bool? value) =>
        new(name, GraphPropertyValueKind.Flag, source, Flag: value);

    public static GraphProperty TimestampValue(string name, string source, DateTimeOffset? value) =>
        new(name, GraphPropertyValueKind.Timestamp, source, Timestamp: value);

    public static GraphProperty JsonValue(string name, string source, string? value) =>
        new(name, GraphPropertyValueKind.Json, source, Json: value);
}

/// <summary>One node and the complete property set that replaces whatever the node held before.</summary>
public sealed record GraphNodePropertySet(GraphEntity Entity, IReadOnlyList<GraphProperty> Properties);

/// <summary>How many rows one property write changed. <see cref="PropertyRowsWritten"/> is the number
/// the projection reports: zero when an ingest found nothing changed (AC-003).</summary>
public sealed record GraphPropertyWriteResult(int Inserted, int Updated, int Deleted, int NodesRemoved)
{
    public int PropertyRowsWritten => Inserted + Updated + Deleted;
}

/// <summary>Value of the property table's <c>source</c> column: which ingest wrote a row.</summary>
public static class GraphPropertySource
{
    public const string DeviceProjection = "device-projection";
}

/// <summary>Property names of the <see cref="GraphEntityKind.Device"/> node.</summary>
public static class DevicePropertyNames
{
    public const string WorkbenchId = "identity.workbenchId";
    public const string WorktreeId = "identity.worktreeId";
    public const string DeviceId = "identity.deviceId";
    public const string PlcName = "identity.plcName";
    public const string EngineeringIdentity = "identity.engineeringIdentity";
    public const string SourceRoot = "source.root";
    public const string SourceProjectPath = "source.projectPath";
    public const string KnowledgeState = "knowledge.state";
    public const string KnowledgeUpdatedAt = "knowledge.updatedAt";
    public const string BlockCount = "counts.blocks";
    public const string SourceObjectCount = "counts.sourceObjects";
    public const string Diagnostics = "diagnostics";

    public const string ExportPlcName = "export.plcName";
    public const string ExportDeviceName = "export.deviceName";
    public const string ExportTypeIdentifier = "export.typeIdentifier";
    public const string ExportProjectName = "export.projectName";
    public const string ExportProjectAuthor = "export.projectAuthor";
    public const string ExportProjectComment = "export.projectComment";
    public const string ExportProjectVersion = "export.projectVersion";
    public const string ExportProjectCopyright = "export.projectCopyright";
    public const string ExportProjectCreationTime = "export.projectCreationTime";
    public const string ExportProjectLastModified = "export.projectLastModified";
    public const string ExportProjectLastModifiedBy = "export.projectLastModifiedBy";
    public const string ExportIsSafetyDevice = "export.isSafetyDevice";
    public const string ExportFSignatureReadState = "export.fSignatureReadState";
    public const string ExportFSignature = "export.fSignature";

    /// <summary>The projection's invalidation flag (ADR-0012): set by a write point that changed the
    /// device's facts, cleared by the projection that re-projected them.</summary>
    public const string ProjectionInvalidated = "projection.invalidated";

    /// <summary>The manifest digest the projection last wrote (ADR-0012): the export root plus a
    /// digest over every projected manifest field, compared at a selection boundary.</summary>
    public const string ProjectionManifestDigest = "projection.manifestDigest";

    /// <summary>
    /// Every worktree that registers this device, as a JSON array of worktree ids sorted ordinally
    /// (AC-007). A device id is registered in more than one worktree — <c>CreateWorktreeAsync</c>
    /// inherits master's device ids into every linked worktree — so a single
    /// <c>graph_entities.worktree_id</c> would be "last writer wins" and deleting one worktree would
    /// remove facts another worktree still owns. The set is the device's ownership; deleting a
    /// worktree only removes it from this set, and the facts go with the last owner.
    /// </summary>
    public const string ProjectionWorktrees = "projection.worktrees";
}

/// <summary>What one reconciliation repair of one device observed (ADR-0012 item 4).</summary>
/// <param name="DeviceId">The device whose projection was inspected.</param>
/// <param name="WasInvalidated">True when a write point had flagged the projection (AC-004's
/// "the invalidation flag" input).</param>
/// <param name="DigestMatched">True when the flagged projection's stored digest still agrees with the
/// manifest on disk (AC-004's "the manifest digest" input); only meaningful when
/// <paramref name="WasInvalidated"/>.</param>
/// <param name="Projection">What the repair's re-projection wrote, or null when nothing was
/// re-projected.</param>
public sealed record DeviceProjectionRepairResult(
    string DeviceId,
    bool WasInvalidated,
    bool DigestMatched,
    DeviceProjectionResult? Projection);

/// <summary>What one worktree-deletion cleanup removed (AC-007).</summary>
/// <param name="RemovedWorktrees">The worktrees the workbench no longer registers and whose facts were
/// removed.</param>
/// <param name="DevicesRemoved">Devices whose facts went with the last owning worktree.</param>
/// <param name="DevicesRetained">Devices another worktree still registers, whose facts were kept.</param>
public sealed record WorktreeFactCleanupResult(
    IReadOnlyList<string> RemovedWorktrees,
    int NodesRemoved,
    int PropertyRowsRemoved,
    int EdgeRowsRemoved,
    IReadOnlyList<string> DevicesRemoved,
    IReadOnlyList<string> DevicesRetained)
{
    public static WorktreeFactCleanupResult Empty { get; } = new([], 0, 0, 0, [], []);
}

/// <summary>Property names of one <see cref="GraphEntityKind.SourceObject"/> node.</summary>
public static class SourceObjectPropertyNames
{
    public const string Id = "source.id";
    public const string Name = "source.name";
    public const string Number = "source.number";
    public const string Category = "source.category";
    public const string ProgrammingLanguage = "source.programmingLanguage";
    public const string GroupPath = "source.groupPath";
    public const string RelativePath = "source.relativePath";
    public const string ContentHash = "source.contentHash";
    public const string IsKnowHowProtected = "source.isKnowHowProtected";
    public const string ModifiedDate = "source.modifiedDate";
    public const string Status = "source.status";
    public const string Fingerprints = "source.fingerprints";
    public const string EvidenceKind = "source.evidenceKind";

    /// <summary>True for a block-category object (Blocks/ or DB/), the device page's block subset.</summary>
    public const string IsBlock = "block.isBlock";

    /// <summary>The block's modified flag as the device page shows it today: always false, the value
    /// the block crawl has always produced (AC-002). Giving it meaning is a later change; the
    /// manifest's <see cref="Status"/> and <see cref="ModifiedDate"/> are stored alongside it.</summary>
    public const string Modified = "block.modified";
}

public sealed class EngineeringGraphConstraintException : InvalidOperationException
{
    public string Code { get; }
    public EngineeringGraphConstraintException(string message, string code = "GRAPH_RELATIONSHIP_INVALID") : base(message) => Code = code;
}

/// <summary>
/// A projection that ran at a selection boundary and failed (ADR-0012, AC-004): the caller must report
/// a projection failure, never serve the stale facts the failed projection was meant to replace, and
/// never dress the failure up as a read failure.
/// </summary>
public sealed class EngineeringGraphProjectionException : InvalidOperationException
{
    public const string FailureCode = "GRAPH_PROJECTION_FAILED";

    public EngineeringGraphProjectionException(string message, Exception? innerException = null)
        : base(message, innerException) => Code = FailureCode;

    public string Code { get; }
}
