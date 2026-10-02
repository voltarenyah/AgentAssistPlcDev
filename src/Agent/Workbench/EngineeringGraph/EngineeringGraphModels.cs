namespace Agent.Workbench.EngineeringGraph;

public enum GraphEntityKind { Task, Session, GitCommit, SourceObject, SvnRevision }
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

public sealed class EngineeringGraphConstraintException : InvalidOperationException
{
    public string Code { get; }
    public EngineeringGraphConstraintException(string message, string code = "GRAPH_RELATIONSHIP_INVALID") : base(message) => Code = code;
}
