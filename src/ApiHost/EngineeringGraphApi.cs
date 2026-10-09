using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Agent.Chat;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class EngineeringGraphApiScope : IDisposable
{
    private readonly EngineeringGraphStore store;
    public EngineeringGraphService Service { get; }

    internal EngineeringGraphApiScope(EngineeringGraphStore store, EngineeringGraphService graph)
    {
        this.store = store;
        Service = graph;
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

/// <summary>One graph entity as the traceability surfaces read it. <c>Tasks</c> and <c>Commits</c>
/// keep their existing meaning (incoming task edges, and the commits that touched this entity).
/// <c>SourceObjects</c> and <c>UnresolvedFiles</c> are additive and are filled for a
/// <c>git_commit</c> entity only: the source objects that commit touched (its outgoing
/// <c>CommitSourceObject</c> evidence edges) and the changed files the evidence indexer could not
/// resolve to a source object. Both are empty for every other entity kind, so no reader can mistake
/// a task's own stage edges for commit evidence.</summary>
public sealed record EngineeringGraphEntityDetailApiResponse(
    string Kind, string Id, string WorkbenchId, string? WorktreeId,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> Tasks,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> Commits,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> SourceObjects,
    IReadOnlyList<string> UnresolvedFiles);

public sealed record EngineeringTaskDetailApiResponse(
    EngineeringTaskApiResponse Task,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> Sessions,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> Commits,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> SourceObjects,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> SvnRevisions);

public sealed record ActiveTaskApiRequest(string? TaskId);

/// <summary>
/// The one place that knows both a conversation's identity and the engineering graph. A conversation's
/// relations to tasks are <c>task_session</c> edges and nothing else (ADR-0014): this class writes the
/// set, projects it into the response shapes, and is the only reader of the legacy session-file field
/// that used to carry the relation.
/// </summary>
public static class SessionGraphOperations
{
    public static Action<EngineeringGraphService, ChatSessionData>? RegisterOverride { get; set; }

    /// <summary>
    /// Test hook for the relation <c>create_task</c> writes after its task write. It mirrors
    /// <see cref="RegisterOverride"/>: the failure has to be injectable from the tool's own call path
    /// so that the degradation the design requires (report the created task, add a
    /// <c>relationWarning</c>) is testable without a second, differently-wired tool.
    /// </summary>
    public static Action<EngineeringGraphService, string, string>? AutomaticRelationOverride { get; set; }

    /// <summary>
    /// Verifies an incoming session header against the trusted device context and returns it with the
    /// trusted identities restored. <c>TaskId</c>/<c>TaskProvenance</c> are deliberately left as the
    /// candidate carries them and are never treated as the relation: the header is not an input to the
    /// relation any more, so an echoed response cannot inject a binding (ADR-0014).
    /// </summary>
    public static ChatSessionData ValidateCandidate(DeviceContext device, ChatSessionData current, ChatSessionData candidate)
    {
        var h = candidate.Header; var t = current.Header;
        if (h.SessionId != t.SessionId || h.WorkbenchId != t.WorkbenchId || h.WorktreeId != t.WorktreeId ||
            h.DeviceId != t.DeviceId || !string.Equals(Path.GetFullPath(h.WorktreeRoot), Path.GetFullPath(t.WorktreeRoot), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(h.KnowledgeDbPath), Path.GetFullPath(t.KnowledgeDbPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Session header does not match the trusted device context.");
        return candidate with { Header = h with { WorkbenchId = t.WorkbenchId, WorktreeId = t.WorktreeId, DeviceId = t.DeviceId, WorktreeRoot = t.WorktreeRoot, KnowledgeDbPath = t.KnowledgeDbPath } };
    }

    /// <summary>
    /// Registers the conversation's graph entity and its primary relation. The primary arrives as an
    /// argument rather than from the session header, which is no longer an input to the relation; the
    /// create routes pass the task they just resolved.
    /// </summary>
    public static void Register(EngineeringGraphService graph, ChatSessionData session, GraphProvenance provenance, string? primaryTaskId)
    {
        RegisterOverride?.Invoke(graph, session);
        try
        {
            graph.RegisterEntity(new GraphEntity(GraphEntityKind.Session, session.Header.SessionId,
                session.Header.WorkbenchId, session.Header.WorktreeId, session.Header.DeviceId));
            if (!string.IsNullOrWhiteSpace(primaryTaskId))
                graph.AddSessionTask(session.Header.SessionId, primaryTaskId, provenance, makePrimaryIfNone: true);
        }
        catch
        {
            graph.RemoveEntity(GraphEntityKind.Session, session.Header.SessionId);
            throw;
        }
    }

    /// <summary>
    /// Relates a conversation's own task creation to the task it created, with provenance
    /// <c>auto</c>: the conversation created it by doing the work. The primary moves only when the
    /// conversation had none (ADR-0014 Decision 2, AC-010). The session entity may not exist yet — an
    /// implicit chat has none — so it is registered here.
    /// </summary>
    public static void RelateAutomatically(EngineeringGraphService graph, DeviceContext device, string sessionId, string taskId)
    {
        AutomaticRelationOverride?.Invoke(graph, sessionId, taskId);
        graph.RegisterEntity(new GraphEntity(GraphEntityKind.Session, sessionId,
            device.WorkbenchId, device.WorktreeId, device.DeviceId));
        graph.AddSessionTask(sessionId, taskId, GraphProvenance.Auto, makePrimaryIfNone: true);
    }

    /// <summary>
    /// Registers the session entity idempotently and replaces its whole relation set in one graph
    /// transaction, answering with the conversation and its projected primary.
    /// </summary>
    public static ChatSessionData SetTasks(
        EngineeringGraphService graph, ChatSessionData session,
        IReadOnlyCollection<string>? taskIds, string? primaryTaskId)
    {
        graph.RegisterEntity(new GraphEntity(GraphEntityKind.Session, session.Header.SessionId,
            session.Header.WorkbenchId, session.Header.WorktreeId, session.Header.DeviceId));
        graph.SetSessionTasks(session.Header.SessionId, taskIds, primaryTaskId);
        return Project(session, graph.ListSessionTaskRelations([session.Header.SessionId]));
    }

    /// <summary>
    /// The body both set routes share, in the order the design fixes: import the legacy header value
    /// once, write the file (which clears the field), then replace the graph set in one transaction,
    /// and answer with the projected primary. File first, so a failure in between can only leave the
    /// requested change unapplied — never fabricate a relation, and never let a cleared set re-import
    /// the old value on the next write (AC-009).
    /// </summary>
    public static ChatSessionData ApplySet(
        EngineeringGraphService graph, ChatSessionData current,
        IReadOnlyCollection<string>? taskIds, string? primaryTaskId,
        Action<ChatSessionData> persist)
    {
        ImportLegacy(graph, current);
        var written = Touch(current);
        persist(written);
        return SetTasks(graph, written, taskIds, primaryTaskId);
    }

    /// <summary>
    /// The body both existing "set the primary relation" routes share. A named task is added to the
    /// set and becomes the primary; a request naming no task removes the conversation's current
    /// primary relation. Neither form deletes the conversation's other relations (AC-012).
    /// </summary>
    public static ChatSessionData SetPrimary(
        EngineeringGraphService graph, ChatSessionData current, string? taskId,
        Action<ChatSessionData> persist)
    {
        ImportLegacy(graph, current);
        var relations = graph.ListSessionTaskRelations([current.Header.SessionId]);
        var taskIds = taskId is null
            ? relations.Where(relation => !relation.IsPrimary).Select(relation => relation.TaskId).ToArray()
            : relations.Select(relation => relation.TaskId).Append(taskId).Distinct(StringComparer.Ordinal).ToArray();
        var written = Touch(current);
        persist(written);
        return SetTasks(graph, written, taskIds, taskId);
    }

    private static ChatSessionData Touch(ChatSessionData session) =>
        session with { Header = session.Header with { UpdatedAt = DateTimeOffset.UtcNow.ToString("O") } };

    /// <summary>
    /// The legacy session-file field's only reader. When the header still carries a task id and the
    /// conversation has no relation at all, that value becomes a relation — primary, with the header's
    /// own provenance. A read never calls this; every write path does, before its write. A value that
    /// cannot become a relation (the task is gone, or it no longer matches the conversation's worktree
    /// or device) is not imported: the per-link validation refuses it, and the write that clears the
    /// field then retires it for good.
    /// </summary>
    public static bool ImportLegacy(EngineeringGraphService graph, ChatSessionData session)
    {
        if (string.IsNullOrWhiteSpace(session.Header.TaskId)) return false;
        try
        {
            graph.RegisterEntity(new GraphEntity(GraphEntityKind.Session, session.Header.SessionId,
                session.Header.WorkbenchId, session.Header.WorktreeId, session.Header.DeviceId));
            if (graph.ListSessionTaskRelations([session.Header.SessionId]).Count > 0) return false;

            var provenance = string.Equals(session.Header.TaskProvenance, "default", StringComparison.OrdinalIgnoreCase)
                ? GraphProvenance.Default
                : GraphProvenance.Manual;
            graph.AddSessionTask(session.Header.SessionId, session.Header.TaskId, provenance, makePrimaryIfNone: true);
            return true;
        }
        catch (EngineeringGraphConstraintException)
        {
            return false;
        }
    }

    /// <summary>
    /// One page's relations in one query, or an empty set when the graph cannot be opened (AC-007). A
    /// conversation list must stay usable: the failure is recorded on the shared log stream the
    /// surfaces already poll, never answered as a 5xx and never allowed to hide a conversation.
    /// </summary>
    public static IReadOnlyList<SessionTaskRelation> ReadRelations(
        EngineeringGraphApiFactory graphs, WorkbenchMetadata workbench,
        IReadOnlyCollection<string>? sessionIds, CompatibilityRuntimeState? logs)
    {
        if (sessionIds is null || sessionIds.Count == 0) return Array.Empty<SessionTaskRelation>();
        try
        {
            using var scope = graphs.Open(workbench);
            return scope.Service.ListSessionTaskRelations(sessionIds);
        }
        catch (Exception exception) when (IsGraphUnavailable(exception))
        {
            logs?.Logs.Enqueue(JsonSerializer.Serialize(new
            {
                kind = "graph-unavailable",
                message = "The engineering graph could not be read; conversations are listed without their task relations.",
                detail = exception.Message,
            }));
            return Array.Empty<SessionTaskRelation>();
        }
    }

    /// <summary>
    /// Whether an exception is the engineering graph being unavailable rather than a rule it enforces.
    /// A rule (<see cref="EngineeringGraphConstraintException"/>) is the caller's answer; an
    /// unavailable store is a degradation the list and per-turn reads must survive.
    /// </summary>
    public static bool IsGraphUnavailable(Exception exception) =>
        exception is SqliteException
            or IOException
            or UnauthorizedAccessException
            or ObjectDisposedException
        || exception is InvalidOperationException and not EngineeringGraphConstraintException;

    /// <summary>The projected relation set and primary for one list row.</summary>
    public static ChatSessionInfo Project(ChatSessionInfo session, IReadOnlyList<SessionTaskRelation> relations)
    {
        var mine = relations.Where(relation => string.Equals(relation.SessionId, session.SessionId, StringComparison.Ordinal)).ToArray();
        var primary = mine.FirstOrDefault(relation => relation.IsPrimary);
        return session with
        {
            TaskId = primary?.TaskId,
            TaskProvenance = ProvenanceName(primary),
            TaskRelations = mine.Select(ToWireRelation).ToArray(),
        };
    }

    /// <summary>
    /// Projects one page of conversations: one relation set per row and the primary into
    /// <c>taskId</c>/<c>taskProvenance</c>, for a page whose relations were read in one query.
    /// </summary>
    public static IReadOnlyList<ChatSessionInfo> ProjectSessions(
        IReadOnlyList<ChatSessionInfo> sessions, IReadOnlyList<SessionTaskRelation> relations) =>
        sessions.Select(session => Project(session, relations)).ToArray();

    /// <summary>
    /// Projects the primary relation onto a returned conversation. The relation set is not carried: a
    /// <c>ChatSessionData</c> is the conversation, not a list row, and two responses of the same
    /// conversation must not be able to disagree about the primary.
    /// </summary>
    public static ChatSessionData Project(ChatSessionData session, IReadOnlyList<SessionTaskRelation> relations)
    {
        var primary = relations.FirstOrDefault(relation =>
            relation.IsPrimary && string.Equals(relation.SessionId, session.Header.SessionId, StringComparison.Ordinal));
        return session with
        {
            Header = session.Header with
            {
                TaskId = primary?.TaskId,
                TaskProvenance = ProvenanceName(primary),
            },
        };
    }

    private static ChatSessionRelation ToWireRelation(SessionTaskRelation relation) =>
        new(relation.TaskId, relation.EdgeId, ProvenanceName(relation)!, relation.IsPrimary);

    /// <summary>The wire form of a provenance: the enum's lowercased name, the shape every existing
    /// relation response already uses.</summary>
    private static string? ProvenanceName(SessionTaskRelation? relation) =>
        relation is null ? null : JsonNamingPolicy.CamelCase.ConvertName(relation.Provenance.ToString());

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
