namespace Agent.Workbench.EngineeringGraph;

public enum GraphEntityKind { Task, Session, GitCommit, SourceObject, SvnRevision }
public enum GraphTaskScopeKind { Project, Worktree }
public enum GraphTaskType { Issue, Improvement, Feature }
public enum GraphTaskStatus { Todo, InProgress, Done }
public enum GraphRelationKind { TaskSession, TaskCommit, TaskSourceObject, TaskSvnRevision, CommitSourceObject, CommitSvnRevision }
public enum GraphProvenance { Manual, Default, Evidence }

public sealed record GraphTask(
    string TaskId, string WorkbenchId, GraphTaskScopeKind ScopeKind, string? WorktreeId,
    string Title, GraphTaskType Type, GraphTaskStatus Status = GraphTaskStatus.Todo,
    string? Description = null, string? MetadataJson = null,
    DateTimeOffset? CreatedUtc = null, DateTimeOffset? UpdatedUtc = null,
    int Priority = 0, string Intent = "", string ExpectedResult = "");

public sealed record GraphEntity(
    GraphEntityKind Kind, string EntityId, string WorkbenchId, string? WorktreeId,
    string? DeviceId = null, string? ExternalRef = null);

public sealed record GraphEdge(
    string EdgeId, GraphEntityKind FromKind, string FromId, GraphEntityKind ToKind, string ToId,
    GraphRelationKind RelationKind, GraphProvenance Provenance, bool IsPrimary,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

public sealed class EngineeringGraphConstraintException : InvalidOperationException
{
    public EngineeringGraphConstraintException(string message) : base(message) { }
}
