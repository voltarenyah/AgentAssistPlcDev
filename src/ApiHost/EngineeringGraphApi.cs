using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Agent.Chat;
using System.Text.Json.Serialization;

public sealed class EngineeringGraphApiScope : IDisposable
{
    private readonly EngineeringGraphStore store;
    public EngineeringGraphService Service { get; }

    internal EngineeringGraphApiScope(EngineeringGraphStore store, EngineeringGraphService service)
    {
        this.store = store;
        Service = service;
    }

    public void Dispose() => store.Dispose();
}

/// <summary>Creates a graph service bound to a server-owned Workbench root.</summary>
public sealed class EngineeringGraphApiFactory
{
    public EngineeringGraphApiScope Open(WorkbenchMetadata workbench)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        var graphStore = new EngineeringGraphStore(workbench.RootPath);
        var graph = new EngineeringGraphService(
            graphStore,
            workbench.WorkbenchId,
            worktreeId => workbench.Worktrees.Any(item => item.WorktreeId == worktreeId));
        return new EngineeringGraphApiScope(graphStore, graph);
    }
}

public sealed record EngineeringTaskApiRequest(
    string Title,
    [property: JsonConverter(typeof(JsonStringEnumConverter<GraphTaskType>))] GraphTaskType Type = GraphTaskType.Feature,
    [property: JsonConverter(typeof(JsonStringEnumConverter<GraphTaskStatus>))] GraphTaskStatus Status = GraphTaskStatus.Todo,
    int Priority = 0,
    string Intent = "",
    string ExpectedResult = "",
    string? Description = null,
    string? DeviceId = null,
    [property: JsonConverter(typeof(JsonStringEnumConverter<GraphTaskTargetKind>))] GraphTaskTargetKind TargetKind = GraphTaskTargetKind.Device);

public sealed record EngineeringTaskApiResponse(
    string TaskId,
    string WorkbenchId,
    string Scope,
    string? WorktreeId,
    string Title,
    string Type,
    string Status,
    int Priority,
    string Intent,
    string ExpectedResult,
    string? Description,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? DeviceId = null,
    string TargetKind = "device");

public sealed record EngineeringTaskUpdateApiRequest(
    string? Title = null,
    [property: JsonConverter(typeof(JsonStringEnumConverter<GraphTaskType>))] GraphTaskType? Type = null,
    [property: JsonConverter(typeof(JsonStringEnumConverter<GraphTaskStatus>))] GraphTaskStatus? Status = null,
    int? Priority = null,
    string? Intent = null,
    string? ExpectedResult = null,
    string? Description = null);

/// <summary>Stage request. <c>BaselineEvidenceJson</c> is accepted for wire compatibility and
/// deliberately ignored: the stage baseline is always derived from the object's committed Git
/// content, so a caller-supplied (live-TIA) baseline can never become task evidence (ADR-0003).</summary>
public sealed record TaskSourceStageApiRequest(string SourceObjectId, string? BaselineEvidenceJson = null);
public sealed record TaskSourceStageApiResponse(string TaskId, string SourceObjectId, string DeviceId, string? BaselineEvidenceJson, DateTimeOffset StagedUtc);

/// <summary>One active stage in a worktree with its owning task, for pickers that must show the
/// current owner before taking a source object over.</summary>
public sealed record WorktreeSourceStageApiResponse(string TaskId, string TaskTitle, string SourceObjectId, string DeviceId, string? BaselineEvidenceJson, DateTimeOffset StagedUtc);

public sealed record EngineeringTaskRelationshipApiResponse(
    string Id,
    string EdgeId,
    string Provenance,
    bool IsPrimary);

public sealed record EngineeringTaskRelationshipApiRequest(
    string TargetKind,
    string TargetId,
    bool IsPrimary = false,
    string? NewTaskId = null,
    string? CurrentEdgeId = null);

public sealed record EngineeringTaskRelationshipMutationApiResponse(
    string EdgeId, string TaskId, string TargetKind, string TargetId, string Relation,
    string Provenance, bool IsPrimary);

public sealed record EngineeringGraphEntityDetailApiResponse(
    string Kind, string Id, string WorkbenchId, string? WorktreeId,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> Tasks,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> Commits);

public sealed record EngineeringTaskDetailApiResponse(
    EngineeringTaskApiResponse Task,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> Sessions,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> Commits,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> SourceObjects,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> SvnRevisions);

public sealed record ActiveTaskApiRequest(string? TaskId);

public static class SessionGraphOperations
{
    public static Action<EngineeringGraphService, ChatSessionData>? RegisterOverride { get; set; }
    public static ChatSessionData ValidateCandidate(DeviceContext device, ChatSessionData current, ChatSessionData candidate)
    {
        var h = candidate.Header; var t = current.Header;
        if (h.SessionId != t.SessionId || h.WorkbenchId != t.WorkbenchId || h.WorktreeId != t.WorktreeId ||
            h.DeviceId != t.DeviceId || !string.Equals(Path.GetFullPath(h.WorktreeRoot), Path.GetFullPath(t.WorktreeRoot), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(h.KnowledgeDbPath), Path.GetFullPath(t.KnowledgeDbPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Session header does not match the trusted device context.");
        return candidate with { Header = h with { WorkbenchId = t.WorkbenchId, WorktreeId = t.WorktreeId, DeviceId = t.DeviceId, WorktreeRoot = t.WorktreeRoot, KnowledgeDbPath = t.KnowledgeDbPath, TaskId = h.TaskId, TaskProvenance = h.TaskProvenance } };
    }
    public static ChatSessionData ApplyWithPersistence(
        EngineeringGraphService graph, ChatSessionData current, string? taskId,
        Func<ChatSessionData, ChatSessionData> update, Action<ChatSessionData> persist)
    {
        var old = graph.GetIncomingEdges(GraphEntityKind.Session, current.Header.SessionId).SingleOrDefault();
        graph.RegisterEntity(new GraphEntity(GraphEntityKind.Session, current.Header.SessionId,
            current.Header.WorkbenchId, current.Header.WorktreeId, current.Header.DeviceId));
        graph.ReplaceSessionTask(current.Header.SessionId, taskId, GraphProvenance.Manual);
        var updated = update(current);
        try { persist(updated); return updated; }
        catch
        {
            graph.ReplaceSessionTask(current.Header.SessionId, old?.FromId, old?.Provenance ?? GraphProvenance.Manual);
            throw;
        }
    }

    public static void Register(EngineeringGraphService graph, ChatSessionData session, GraphProvenance provenance)
    {
        RegisterOverride?.Invoke(graph, session);
        try
        {
            graph.RegisterEntity(new GraphEntity(GraphEntityKind.Session, session.Header.SessionId,
                session.Header.WorkbenchId, session.Header.WorktreeId, session.Header.DeviceId));
            if (!string.IsNullOrWhiteSpace(session.Header.TaskId))
            {
                var task = graph.FindTask(session.Header.TaskId)
                    ?? throw new EngineeringGraphConstraintException("The selected task was not found in the current Workbench.");
                if (task.ScopeKind == GraphTaskScopeKind.Worktree && task.WorktreeId != session.Header.WorktreeId)
                    throw new EngineeringGraphConstraintException("The selected task is not compatible with the current project or Workbench context.");
                graph.AddEdge(GraphEntityKind.Task, task.TaskId, GraphEntityKind.Session, session.Header.SessionId, provenance);
            }
        }
        catch
        {
            graph.RemoveEntity(GraphEntityKind.Session, session.Header.SessionId);
            throw;
        }
    }

    public static void SetTask(EngineeringGraphService graph, ChatSessionData session, string? taskId)
    {
        graph.RegisterEntity(new GraphEntity(GraphEntityKind.Session, session.Header.SessionId,
            session.Header.WorkbenchId, session.Header.WorktreeId, session.Header.DeviceId));
        graph.ReplaceSessionTask(session.Header.SessionId, taskId, GraphProvenance.Manual);
    }

    /// <summary>
    /// Deletes a conversation from both stores it lives in, graph first: the entity and its edges go
    /// before the session file, so a failure in between leaves an orphan file that nothing points at
    /// rather than a task detail listing a conversation that can no longer be loaded (ADR-0010).
    /// Both delete routes call this, so the order cannot be reimplemented differently.
    /// </summary>
    internal static void Delete(EngineeringGraphService graph, ApiChatService chat, DeviceContext device,
        string sessionId, string scope)
    {
        graph.RemoveEntity(GraphEntityKind.Session, sessionId);
        chat.DeleteSession(device, sessionId, scope);
    }
}
