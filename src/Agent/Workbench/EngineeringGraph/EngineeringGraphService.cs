using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Workbench.EngineeringGraph;

public sealed class EngineeringGraphService
{
    private readonly EngineeringGraphStore _store;
    private readonly string _workbenchId;
    private readonly Func<string, bool> _worktreeExists;

    private static readonly IReadOnlyDictionary<(GraphEntityKind, GraphEntityKind), GraphRelationKind> Relations =
        new Dictionary<(GraphEntityKind, GraphEntityKind), GraphRelationKind>
        {
            [(GraphEntityKind.Task, GraphEntityKind.Session)] = GraphRelationKind.TaskSession,
            [(GraphEntityKind.Task, GraphEntityKind.GitCommit)] = GraphRelationKind.TaskCommit,
            [(GraphEntityKind.Task, GraphEntityKind.SourceObject)] = GraphRelationKind.TaskSourceObject,
            [(GraphEntityKind.Task, GraphEntityKind.SvnRevision)] = GraphRelationKind.TaskSvnRevision,
            [(GraphEntityKind.GitCommit, GraphEntityKind.SourceObject)] = GraphRelationKind.CommitSourceObject,
            [(GraphEntityKind.GitCommit, GraphEntityKind.SvnRevision)] = GraphRelationKind.CommitSvnRevision,
        };

    public EngineeringGraphService(EngineeringGraphStore store, string workbenchId,
        Func<string, bool>? worktreeExists = null)
    {
        _store = store;
        _workbenchId = Require(workbenchId, nameof(workbenchId));
        _worktreeExists = worktreeExists ?? (_ => false);
    }
    public string WorkbenchId() => _workbenchId;

    public GraphTask CreateTask(string taskId, GraphTaskScopeKind scope, string? worktreeId, string title, GraphTaskType type,
        GraphTaskStatus status = GraphTaskStatus.Todo, string? description = null, int priority = 0,
        string intent = "", string expectedResult = "", string? deviceId = null)
    {
        taskId = Require(taskId, nameof(taskId)); title = Require(title, nameof(title));
        if (scope == GraphTaskScopeKind.Worktree && string.IsNullOrWhiteSpace(worktreeId))
            throw new EngineeringGraphConstraintException("A worktree-scoped task requires a worktree ID.");
        if (scope == GraphTaskScopeKind.Worktree && string.IsNullOrWhiteSpace(deviceId))
            throw new EngineeringGraphConstraintException("A worktree-scoped task requires one PLC device.", "TASK_DEVICE_REQUIRED");
        if (worktreeId is not null && !_worktreeExists(worktreeId))
            throw new EngineeringGraphConstraintException($"Worktree '{worktreeId}' is not registered in the current Workbench.");
        if (scope == GraphTaskScopeKind.Project && worktreeId is not null)
            throw new EngineeringGraphConstraintException("A project-scoped task cannot have a worktree ID.");
        if (scope != GraphTaskScopeKind.Worktree && deviceId is not null)
            throw new EngineeringGraphConstraintException("Only worktree tasks can bind a PLC device.");
        var now = DateTimeOffset.UtcNow;
        intent = Require(intent, nameof(intent));
        expectedResult = Require(expectedResult, nameof(expectedResult));
        var task = new GraphTask(taskId, _workbenchId, scope, worktreeId, title, type, status, description,
            JsonSerializer.Serialize(new { priority, intent, expectedResult }), now, now, priority, intent, expectedResult, deviceId);
        using var tx = _store.Connection.BeginTransaction();
        Execute(tx, """
            INSERT INTO tasks (task_id, workbench_id, scope_kind, worktree_id, device_id, type, status, title, description, metadata_json, created_utc, updated_utc)
            VALUES ($id,$wb,$scope,$wt,$device,$type,$status,$title,$description,$metadata,$created,$updated);
            INSERT INTO graph_entities (entity_kind, entity_id, workbench_id, worktree_id) VALUES ('task',$id,$wb,$wt);
            """, ("$id", task.TaskId), ("$wb", _workbenchId), ("$scope", task.ScopeKind.ToString().ToLowerInvariant()),
            ("$wt", task.WorktreeId), ("$device", task.DeviceId), ("$type", task.Type.ToString().ToLowerInvariant()), ("$status", task.Status.ToString().ToLowerInvariant()),
            ("$title", task.Title), ("$description", task.Description), ("$metadata", task.MetadataJson),
            ("$created", now.ToString("O")), ("$updated", now.ToString("O")));
        tx.Commit();
        return task;
    }

    public TaskSourceStage StageSourceObject(string taskId, string sourceObjectId, string? baselineEvidenceJson = null)
    {
        var task = FindTask(taskId) ?? throw new EngineeringGraphConstraintException("Task was not found.", "TASK_NOT_FOUND");
        if (task.ScopeKind != GraphTaskScopeKind.Worktree || string.IsNullOrWhiteSpace(task.DeviceId))
            throw new EngineeringGraphConstraintException("A task must bind one device before staging source objects.", "TASK_DEVICE_REQUIRED");
        var source = FindEntity(GraphEntityKind.SourceObject, sourceObjectId)
            ?? throw new EngineeringGraphConstraintException("Source object was not registered.", "GRAPH_TARGET_NOT_FOUND");
        if (source.WorktreeId != task.WorktreeId || source.DeviceId != task.DeviceId)
            throw new EngineeringGraphConstraintException("A task can stage only source objects from its bound device.", "TASK_SOURCE_DEVICE_MISMATCH");
        var now = DateTimeOffset.UtcNow;
        using var tx = _store.Connection.BeginTransaction();
        try
        {
            Execute(tx, """
                INSERT INTO task_source_stages (task_id,worktree_id,source_object_id,device_id,baseline_evidence_json,staged_utc,released_utc)
                VALUES ($task,$worktree,$source,$device,$evidence,$utc,NULL)
                ON CONFLICT(task_id,source_object_id) DO UPDATE SET
                    worktree_id=excluded.worktree_id, device_id=excluded.device_id,
                    baseline_evidence_json=excluded.baseline_evidence_json, staged_utc=excluded.staged_utc, released_utc=NULL;
                INSERT OR IGNORE INTO graph_edges (edge_id,from_kind,from_id,to_kind,to_id,relation_kind,provenance,is_primary,created_utc,updated_utc)
                VALUES ($edge,'task',$task,'source_object',$source,'task_source_object','manual',0,$utc,$utc);
                """, ("$task", taskId), ("$worktree", task.WorktreeId!), ("$source", sourceObjectId),
                ("$device", task.DeviceId), ("$evidence", baselineEvidenceJson), ("$utc", now.ToString("O")), ("$edge", Guid.NewGuid().ToString("N")));
            tx.Commit();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new EngineeringGraphConstraintException("This source object is already staged by another active task.", "SOURCE_ALREADY_STAGED");
        }
        return new TaskSourceStage(taskId, task.WorktreeId!, sourceObjectId, task.DeviceId, baselineEvidenceJson, now);
    }

    public IReadOnlyList<TaskSourceStage> ListActiveStages(string taskId)
    {
        using var command = _store.Connection.CreateCommand(); command.CommandText = "SELECT worktree_id,source_object_id,device_id,baseline_evidence_json,staged_utc FROM task_source_stages WHERE task_id=$task AND released_utc IS NULL ORDER BY source_object_id;"; command.Parameters.AddWithValue("$task", taskId);
        using var reader = command.ExecuteReader(); var result = new List<TaskSourceStage>();
        while (reader.Read()) result.Add(new TaskSourceStage(taskId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4))));
        return result;
    }

    public void ReleaseTaskStages(string taskId) => ExecuteNonQuery("UPDATE task_source_stages SET released_utc=$utc WHERE task_id=$task AND released_utc IS NULL;", ("$task", taskId), ("$utc", DateTimeOffset.UtcNow.ToString("O")));

    public bool ReleaseSourceStage(string taskId, string sourceObjectId) =>
        ExecuteNonQuery("UPDATE task_source_stages SET released_utc=$utc WHERE task_id=$task AND source_object_id=$source AND released_utc IS NULL;", ("$task", taskId), ("$source", sourceObjectId), ("$utc", DateTimeOffset.UtcNow.ToString("O"))) > 0;

    public bool UpdateStageEvidence(string taskId, string sourceObjectId, string baselineEvidenceJson) =>
        ExecuteNonQuery("UPDATE task_source_stages SET baseline_evidence_json=$evidence WHERE task_id=$task AND source_object_id=$source AND released_utc IS NULL;", ("$task", taskId), ("$source", sourceObjectId), ("$evidence", baselineEvidenceJson)) > 0;

    public void ImportTask(GraphTask task, IReadOnlyList<string> elementRefs, DateTimeOffset? doneUtc, string sourceId)
    {
        if (HasLegacyImport(sourceId)) return;
        if (FindTask(task.TaskId) is not null)
            throw new EngineeringGraphConstraintException($"Legacy task ID '{task.TaskId}' already exists; import was not applied.");
        if (task.WorkbenchId != _workbenchId || task.ScopeKind != GraphTaskScopeKind.Worktree || task.WorktreeId is null || !_worktreeExists(task.WorktreeId))
            throw new EngineeringGraphConstraintException("Legacy task has invalid Workbench or Worktree scope.");
        var metadata = JsonSerializer.Serialize(new { priority = task.Priority, intent = task.Intent, expectedResult = task.ExpectedResult, elementRefs, doneUtc });
        using var tx = _store.Connection.BeginTransaction();
        Execute(tx, "INSERT INTO tasks (task_id,workbench_id,scope_kind,worktree_id,type,status,title,description,metadata_json,created_utc,updated_utc) VALUES ($id,$wb,'worktree',$wt,$type,$status,$title,$description,$metadata,$created,$updated); INSERT INTO graph_entities (entity_kind,entity_id,workbench_id,worktree_id) VALUES ('task',$id,$wb,$wt); INSERT INTO legacy_imports (source_kind,source_id,imported_utc) VALUES ('tasks.json',$source,$imported);",
            ("$id", task.TaskId), ("$wb", task.WorkbenchId), ("$wt", task.WorktreeId), ("$type", task.Type.ToString().ToLowerInvariant()),
            ("$status", task.Status.ToString().ToLowerInvariant()), ("$title", task.Title), ("$description", task.Description), ("$metadata", metadata),
            ("$created", task.CreatedUtc!.Value.ToString("O")), ("$updated", (task.UpdatedUtc ?? task.CreatedUtc)!.Value.ToString("O")),
            ("$source", sourceId), ("$imported", DateTimeOffset.UtcNow.ToString("O")));
        tx.Commit();
    }

    public IReadOnlyList<GraphTask> ListTasks(string? worktreeId = null)
    {
        using var command = _store.Connection.CreateCommand();
        command.CommandText = "SELECT task_id FROM tasks WHERE workbench_id=$wb" +
            (worktreeId is null ? "" : " AND worktree_id=$wt") + " ORDER BY created_utc;";
        command.Parameters.AddWithValue("$wb", _workbenchId);
        if (worktreeId is not null) command.Parameters.AddWithValue("$wt", worktreeId);
        var ids = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetString(0));
        return ids.Select(id => GetTask(id)!).ToArray();
    }

    public GraphTask? UpdateTask(string taskId, Func<GraphTask, GraphTask> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var current = GetTask(taskId);
        if (current is null) return null;
        var updated = change(current) with { UpdatedUtc = DateTimeOffset.UtcNow };
        if (updated.WorkbenchId != _workbenchId || updated.TaskId != current.TaskId ||
            updated.ScopeKind != current.ScopeKind || updated.WorktreeId != current.WorktreeId || updated.DeviceId != current.DeviceId)
            throw new EngineeringGraphConstraintException("Task identity and scope cannot be changed.");
        var metadata = UpdatedMetadataJson(updated);
        ExecuteNonQuery("UPDATE tasks SET title=$title,description=$description,type=$type,status=$status,metadata_json=$metadata,updated_utc=$updated WHERE task_id=$id AND workbench_id=$wb",
            ("$title", updated.Title), ("$description", updated.Description), ("$status", updated.Status.ToString().ToLowerInvariant()),
            ("$type", updated.Type.ToString().ToLowerInvariant()),
            ("$metadata", metadata), ("$updated", updated.UpdatedUtc!.Value.ToString("O")), ("$id", taskId), ("$wb", _workbenchId));
        if (current.Status != GraphTaskStatus.Done && updated.Status == GraphTaskStatus.Done)
            ReleaseTaskStages(taskId);
        return updated with { MetadataJson = metadata };
    }

    public bool HasLegacyImport(string sourceId) => Convert.ToInt32(Scalar(
        "SELECT COUNT(*) FROM legacy_imports WHERE source_kind='tasks.json' AND source_id=$id;", ("$id", sourceId))) != 0;

    public void MarkLegacyImport(string sourceId) => ExecuteNonQuery(
        "INSERT OR IGNORE INTO legacy_imports (source_kind,source_id,imported_utc) VALUES ('tasks.json',$id,$utc);",
        ("$id", sourceId), ("$utc", DateTimeOffset.UtcNow.ToString("O")));

    public GraphTask? FindTask(string taskId) => GetTask(taskId);

    public bool DeleteTask(string taskId)
    {
        using var tx = _store.Connection.BeginTransaction();
        using var command = _store.Connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM task_source_stages WHERE task_id=$id; DELETE FROM tasks WHERE task_id=$id AND workbench_id=$wb; DELETE FROM graph_entities WHERE entity_kind='task' AND entity_id=$id;";
        command.Parameters.AddWithValue("$id", taskId); command.Parameters.AddWithValue("$wb", _workbenchId);
        var removed = command.ExecuteNonQuery() > 0;
        tx.Commit();
        return removed;
    }

    public void RegisterEntity(GraphEntity entity)
    {
        if (entity.Kind == GraphEntityKind.Task) throw new EngineeringGraphConstraintException("Tasks must be created with CreateTask.");
        if (entity.WorkbenchId != _workbenchId) throw new EngineeringGraphConstraintException("Entity belongs to another Workbench.");
        if (entity.WorktreeId is not null && !_worktreeExists(entity.WorktreeId))
            throw new EngineeringGraphConstraintException($"Worktree '{entity.WorktreeId}' is not registered in the current Workbench.");
        Require(entity.EntityId, nameof(entity.EntityId));
        ExecuteNonQuery("INSERT OR REPLACE INTO graph_entities (entity_kind,entity_id,workbench_id,worktree_id,device_id,external_ref) VALUES ($kind,$id,$wb,$wt,$device,$ref)",
            ("$kind", Kind(entity.Kind)), ("$id", entity.EntityId), ("$wb", entity.WorkbenchId), ("$wt", entity.WorktreeId),
            ("$device", entity.DeviceId), ("$ref", entity.ExternalRef));
    }

    public GraphEdge AddEdge(GraphTask task, GraphEntityKind toKind, string toId,
        GraphProvenance provenance = GraphProvenance.Manual, bool isPrimary = false) =>
        AddEdge(GraphEntityKind.Task, task.TaskId, toKind, toId, provenance, isPrimary);

    public GraphEdge AddEdge(GraphEntityKind fromKind, string fromId, GraphEntityKind toKind, string toId,
        GraphProvenance provenance = GraphProvenance.Manual, bool isPrimary = false)
    {
        if (!Relations.TryGetValue((fromKind, toKind), out var relation))
            throw new EngineeringGraphConstraintException($"Unsupported relationship: {fromKind} -> {toKind}.");
        var from = FindEntity(fromKind, fromId) ?? throw new EngineeringGraphConstraintException("Source entity was not registered.", "GRAPH_SOURCE_NOT_FOUND");
        var to = FindEntity(toKind, toId) ?? throw new EngineeringGraphConstraintException("Target entity was not registered.", "GRAPH_TARGET_NOT_FOUND");
        if (from.WorkbenchId != _workbenchId || to.WorkbenchId != _workbenchId)
            throw new EngineeringGraphConstraintException("Entities must belong to the current Workbench.");
        if (fromKind == GraphEntityKind.Task && IsWorktreeTask(fromId) && from.WorktreeId != to.WorktreeId)
            throw new EngineeringGraphConstraintException("A worktree-scoped task can only link within its Worktree.");
        if (isPrimary && relation != GraphRelationKind.TaskCommit)
            throw new EngineeringGraphConstraintException("Only task-to-commit relationships may be primary.");
        var now = DateTimeOffset.UtcNow;
        var edge = new GraphEdge(Guid.NewGuid().ToString("N"), fromKind, fromId, toKind, toId, relation, provenance, isPrimary, now, now);
        try
        {
            ExecuteNonQuery("INSERT INTO graph_edges (edge_id,from_kind,from_id,to_kind,to_id,relation_kind,provenance,is_primary,created_utc,updated_utc) VALUES ($edge,$fk,$fid,$tk,$tid,$relation,$prov,$primary,$created,$updated)",
                ("$edge", edge.EdgeId), ("$fk", Kind(fromKind)), ("$fid", fromId), ("$tk", Kind(toKind)), ("$tid", toId),
                ("$relation", Relation(relation)), ("$prov", provenance.ToString().ToLowerInvariant()), ("$primary", isPrimary ? 1 : 0),
                ("$created", now.ToString("O")), ("$updated", now.ToString("O")));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 19)
        {
            throw new EngineeringGraphConstraintException(
                isPrimary ? "The target already has a primary task relationship." : "The relationship already exists.",
                isPrimary ? "GRAPH_PRIMARY_RELATIONSHIP_EXISTS" : "GRAPH_RELATIONSHIP_EXISTS");
        }
        return edge;
    }

    public void RemoveEdge(string edgeId)
    {
        if (string.IsNullOrWhiteSpace(edgeId)) return;
        ExecuteNonQuery("DELETE FROM graph_edges WHERE edge_id=$id", ("$id", edgeId));
    }

    public GraphEntity? GetEntity(GraphEntityKind kind, string entityId) => FindEntity(kind, entityId);

    public GraphEdge? ReplaceTaskRelationship(string taskId, GraphEntityKind targetKind, string targetId,
        GraphProvenance provenance = GraphProvenance.Manual, bool isPrimary = false)
    {
        var task = FindTask(taskId) ?? throw new EngineeringGraphConstraintException("Task was not found in the current Workbench.", "TASK_NOT_FOUND");
        var target = FindEntity(targetKind, targetId) ?? throw new EngineeringGraphConstraintException("Target entity was not registered in the current Workbench.", "GRAPH_TARGET_NOT_FOUND");
        if (targetKind == GraphEntityKind.Task || !Relations.ContainsKey((GraphEntityKind.Task, targetKind)))
            throw new EngineeringGraphConstraintException("The target kind is not a supported task relationship.");
        if (isPrimary && targetKind != GraphEntityKind.GitCommit)
            throw new EngineeringGraphConstraintException("Only task-to-commit relationships may be primary.");
        if (task.ScopeKind == GraphTaskScopeKind.Worktree && task.WorktreeId != target.WorktreeId)
            throw new EngineeringGraphConstraintException("A worktree-scoped task can only link within its Worktree.");
        using var tx = _store.Connection.BeginTransaction();
        var deleteSql = targetKind == GraphEntityKind.Session
            ? "DELETE FROM graph_edges WHERE from_kind='task' AND to_kind='session' AND to_id=$target;"
            : "DELETE FROM graph_edges WHERE from_kind='task' AND from_id=$task AND to_kind=$kind AND to_id=$target;";
        Execute(tx, deleteSql, ("$task", taskId), ("$kind", Kind(targetKind)), ("$target", targetId));
        var now = DateTimeOffset.UtcNow;
        var edge = new GraphEdge(Guid.NewGuid().ToString("N"), GraphEntityKind.Task, taskId, targetKind, targetId,
            Relations[(GraphEntityKind.Task, targetKind)], provenance, isPrimary, now, now);
        try
        {
            Execute(tx, "INSERT INTO graph_edges (edge_id,from_kind,from_id,to_kind,to_id,relation_kind,provenance,is_primary,created_utc,updated_utc) VALUES ($edge,'task',$task,$kind,$target,$relation,$prov,$primary,$created,$updated)",
                ("$edge", edge.EdgeId), ("$task", taskId), ("$kind", Kind(targetKind)), ("$target", targetId),
                ("$relation", Relation(edge.RelationKind)), ("$prov", provenance.ToString().ToLowerInvariant()), ("$primary", isPrimary ? 1 : 0),
                ("$created", now.ToString("O")), ("$updated", now.ToString("O")));
            tx.Commit();
            return edge;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 19)
        {
            throw new EngineeringGraphConstraintException("The target already has a primary task relationship.", "GRAPH_PRIMARY_RELATIONSHIP_EXISTS");
        }
    }

    public GraphEdge ReassignTaskRelationship(string currentTaskId, string replacementTaskId, GraphEntityKind targetKind, string targetId, string currentEdgeId,
        GraphProvenance provenance = GraphProvenance.Manual, bool isPrimary = false)
    {
        var current = FindTask(currentTaskId) ?? throw new EngineeringGraphConstraintException("Task was not found in the current Workbench.", "TASK_NOT_FOUND");
        var replacement = FindTask(replacementTaskId) ?? throw new EngineeringGraphConstraintException("Replacement task was not found in the current Workbench.", "TASK_NOT_FOUND");
        var target = FindEntity(targetKind, targetId) ?? throw new EngineeringGraphConstraintException("Target entity was not registered in the current Workbench.", "GRAPH_TARGET_NOT_FOUND");
        if (!Relations.ContainsKey((GraphEntityKind.Task, targetKind))) throw new EngineeringGraphConstraintException("The target kind is not a supported task relationship.");
        if (replacement.ScopeKind == GraphTaskScopeKind.Worktree && replacement.WorktreeId != target.WorktreeId) throw new EngineeringGraphConstraintException("A worktree-scoped task can only link within its Worktree.");
        var old = GetEdges(GraphEntityKind.Task, currentTaskId, targetKind).SingleOrDefault(edge => edge.EdgeId == currentEdgeId && edge.ToId == targetId)
            ?? throw new EngineeringGraphConstraintException("The relationship was not found in the current Workbench.", "RELATIONSHIP_NOT_FOUND");
        using var tx = _store.Connection.BeginTransaction();
        Execute(tx, "DELETE FROM graph_edges WHERE edge_id=$edge", ("$edge", old.EdgeId));
        var now = DateTimeOffset.UtcNow;
        var edge = new GraphEdge(Guid.NewGuid().ToString("N"), GraphEntityKind.Task, replacementTaskId, targetKind, targetId, Relations[(GraphEntityKind.Task, targetKind)], provenance, isPrimary, now, now);
        Execute(tx, "INSERT INTO graph_edges (edge_id,from_kind,from_id,to_kind,to_id,relation_kind,provenance,is_primary,created_utc,updated_utc) VALUES ($edge,'task',$task,$kind,$target,$relation,$prov,$primary,$created,$updated)",
            ("$edge", edge.EdgeId), ("$task", replacementTaskId), ("$kind", Kind(targetKind)), ("$target", targetId), ("$relation", Relation(edge.RelationKind)), ("$prov", provenance.ToString().ToLowerInvariant()), ("$primary", isPrimary ? 1 : 0), ("$created", now.ToString("O")), ("$updated", now.ToString("O")));
        tx.Commit();
        return edge;
    }
    public void RemoveEntity(GraphEntityKind kind, string entityId)
    {
        ExecuteNonQuery("DELETE FROM graph_edges WHERE from_kind=$kind AND from_id=$id OR to_kind=$kind AND to_id=$id", ("$kind", Kind(kind)), ("$id", entityId));
        ExecuteNonQuery("DELETE FROM graph_entities WHERE entity_kind=$kind AND entity_id=$id", ("$kind", Kind(kind)), ("$id", entityId));
    }

    public GraphEdge? ReplaceSessionTask(string sessionId, string? taskId, GraphProvenance provenance)
    {
        var session = FindEntity(GraphEntityKind.Session, sessionId)
            ?? throw new EngineeringGraphConstraintException("Session was not registered in the current Workbench.");
        GraphTask? task = null;
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            task = FindTask(taskId) ?? throw new EngineeringGraphConstraintException("The selected task was not found in the current Workbench.");
            if (task.ScopeKind == GraphTaskScopeKind.Worktree && task.WorktreeId != session.WorktreeId)
                throw new EngineeringGraphConstraintException("The selected task is not compatible with the current project or Workbench context.");
        }
        using var tx = _store.Connection.BeginTransaction();
        Execute(tx, "DELETE FROM graph_edges WHERE to_kind='session' AND to_id=$session", ("$session", sessionId));
        if (task is null)
        {
            tx.Commit();
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        var edge = new GraphEdge(Guid.NewGuid().ToString("N"), GraphEntityKind.Task, task.TaskId,
            GraphEntityKind.Session, sessionId, GraphRelationKind.TaskSession, provenance, false, now, now);
        Execute(tx, "INSERT INTO graph_edges (edge_id,from_kind,from_id,to_kind,to_id,relation_kind,provenance,is_primary,created_utc,updated_utc) VALUES ($edge,'task',$task,'session',$session,'task_session',$prov,0,$created,$updated)",
            ("$edge", edge.EdgeId), ("$task", task.TaskId), ("$session", sessionId),
            ("$prov", provenance.ToString().ToLowerInvariant()), ("$created", now.ToString("O")), ("$updated", now.ToString("O")));
        tx.Commit();
        return edge;
    }

    public int CountEdges() => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM graph_edges;"));
    public void RecordFileEvidence(string commitSha, string relativePath)
    {
        ExecuteNonQuery("INSERT OR IGNORE INTO graph_file_evidence (commit_sha,relative_path,recorded_utc) VALUES ($sha,$path,$utc);",
            ("$sha", commitSha), ("$path", relativePath), ("$utc", DateTimeOffset.UtcNow.ToString("O")));
    }
    public IReadOnlyList<GraphFileEvidence> GetFileEvidence(string commitSha)
    {
        using var c = _store.Connection.CreateCommand();
        c.CommandText = "SELECT commit_sha,relative_path,recorded_utc FROM graph_file_evidence WHERE commit_sha=$sha ORDER BY relative_path;";
        c.Parameters.AddWithValue("$sha", commitSha);
        using var r = c.ExecuteReader();
        var result = new List<GraphFileEvidence>();
        while (r.Read()) result.Add(new GraphFileEvidence(r.GetString(0), r.GetString(1), DateTimeOffset.Parse(r.GetString(2))));
        return result;
    }
    public GraphTask? GetTask(string taskId)
    {
        using var c = _store.Connection.CreateCommand();
        c.CommandText = "SELECT workbench_id,scope_kind,worktree_id,device_id,title,type,status,description,metadata_json,created_utc,updated_utc FROM tasks WHERE task_id=$id AND workbench_id=$wb;";
        c.Parameters.AddWithValue("$id", taskId);
        c.Parameters.AddWithValue("$wb", _workbenchId);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        var metadata = r.IsDBNull(8) ? null : JsonSerializer.Deserialize<TaskMetadata>(r.GetString(8),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return new GraphTask(taskId, r.GetString(0), Enum.Parse<GraphTaskScopeKind>(r.GetString(1), true),
            r.IsDBNull(2) ? null : r.GetString(2), r.GetString(4), Enum.Parse<GraphTaskType>(r.GetString(5), true),
            Enum.Parse<GraphTaskStatus>(r.GetString(6), true), r.IsDBNull(7) ? null : r.GetString(7),
            r.IsDBNull(8) ? null : r.GetString(8), DateTimeOffset.Parse(r.GetString(9)), DateTimeOffset.Parse(r.GetString(10)),
            metadata?.Priority ?? 0, metadata?.Intent ?? "", metadata?.ExpectedResult ?? "", r.IsDBNull(3) ? null : r.GetString(3));
    }
    public IReadOnlyList<GraphEdge> GetEdges(GraphEntityKind fromKind, string fromId, GraphEntityKind? toKind = null) =>
        ReadEdges(fromKind, fromId, toKind);

    public IReadOnlyList<GraphEdge> GetIncomingEdges(GraphEntityKind toKind, string toId)
    {
        using var c = _store.Connection.CreateCommand();
        c.CommandText = "SELECT edge_id,from_kind,from_id,relation_kind,provenance,is_primary,created_utc,updated_utc FROM graph_edges WHERE to_kind=$tk AND to_id=$id ORDER BY relation_kind,from_id,created_utc;";
        c.Parameters.AddWithValue("$tk", Kind(toKind)); c.Parameters.AddWithValue("$id", toId);
        using var reader = c.ExecuteReader();
        var result = new List<GraphEdge>();
        while (reader.Read())
            result.Add(new GraphEdge(reader.GetString(0), ParseKind(reader.GetString(1)), reader.GetString(2), toKind, toId,
                ParseRelation(reader.GetString(3)), ParseProvenance(reader.GetString(4)), reader.GetInt32(5) != 0,
                DateTimeOffset.Parse(reader.GetString(6)), DateTimeOffset.Parse(reader.GetString(7))));
        return result;
    }

    private IReadOnlyList<GraphEdge> ReadEdges(GraphEntityKind fromKind, string fromId, GraphEntityKind? toKind)
    {
        using var c = _store.Connection.CreateCommand();
        c.CommandText = "SELECT edge_id,to_kind,to_id,relation_kind,provenance,is_primary,created_utc,updated_utc FROM graph_edges WHERE from_kind=$fk AND from_id=$id" +
            (toKind is null ? "" : " AND to_kind=$tk") + " ORDER BY relation_kind,to_id,created_utc;";
        c.Parameters.AddWithValue("$fk", Kind(fromKind)); c.Parameters.AddWithValue("$id", fromId);
        if (toKind is not null) c.Parameters.AddWithValue("$tk", Kind(toKind.Value));
        using var reader = c.ExecuteReader();
        var result = new List<GraphEdge>();
        while (reader.Read())
            result.Add(new GraphEdge(reader.GetString(0), fromKind, fromId, ParseKind(reader.GetString(1)), reader.GetString(2),
                ParseRelation(reader.GetString(3)), ParseProvenance(reader.GetString(4)), reader.GetInt32(5) != 0,
                DateTimeOffset.Parse(reader.GetString(6)), DateTimeOffset.Parse(reader.GetString(7))));
        return result;
    }

    private bool IsWorktreeTask(string id) => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM tasks WHERE task_id=$id AND scope_kind='worktree';", ("$id", id))) == 1;
    private GraphEntity? FindEntity(GraphEntityKind kind, string id)
    {
        using var c = _store.Connection.CreateCommand(); c.CommandText = "SELECT workbench_id,worktree_id,device_id,external_ref FROM graph_entities WHERE entity_kind=$kind AND entity_id=$id;";
        c.Parameters.AddWithValue("$kind", Kind(kind)); c.Parameters.AddWithValue("$id", id);
        using var r = c.ExecuteReader(); return r.Read() ? new GraphEntity(kind, id, r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)) : null;
    }
    private long Scalar(string sql, params (string Name, object? Value)[] ps) { using var c=_store.Connection.CreateCommand(); c.CommandText=sql; foreach(var p in ps)c.Parameters.AddWithValue(p.Name,p.Value??DBNull.Value); return Convert.ToInt64(c.ExecuteScalar()); }
    private int ExecuteNonQuery(string sql, params (string Name, object? Value)[] ps) { using var c=_store.Connection.CreateCommand(); c.CommandText=sql; foreach(var p in ps)c.Parameters.AddWithValue(p.Name,p.Value??DBNull.Value); return c.ExecuteNonQuery(); }
    private void Execute(SqliteTransaction tx,string sql, params (string Name, object? Value)[] ps) { using var c=_store.Connection.CreateCommand(); c.Transaction=tx;c.CommandText=sql;foreach(var p in ps)c.Parameters.AddWithValue(p.Name,p.Value??DBNull.Value);c.ExecuteNonQuery(); }
    private static string Kind(GraphEntityKind k)=>k switch { GraphEntityKind.GitCommit=>"git_commit",GraphEntityKind.SourceObject=>"source_object",GraphEntityKind.SvnRevision=>"svn_revision",GraphEntityKind.Session=>"session", _=>"task" };
    private static GraphEntityKind ParseKind(string value)=>value switch { "git_commit"=>GraphEntityKind.GitCommit,"source_object"=>GraphEntityKind.SourceObject,"svn_revision"=>GraphEntityKind.SvnRevision,"session"=>GraphEntityKind.Session,_=>GraphEntityKind.Task };
    private static string Relation(GraphRelationKind k)=>k switch { GraphRelationKind.TaskSession=>"task_session",GraphRelationKind.TaskCommit=>"task_commit",GraphRelationKind.TaskSourceObject=>"task_source_object",GraphRelationKind.TaskSvnRevision=>"task_svn_revision",GraphRelationKind.CommitSourceObject=>"commit_source_object", _=>"commit_svn_revision" };
    private static GraphRelationKind ParseRelation(string value)=>value switch { "task_session"=>GraphRelationKind.TaskSession,"task_commit"=>GraphRelationKind.TaskCommit,"task_source_object"=>GraphRelationKind.TaskSourceObject,"task_svn_revision"=>GraphRelationKind.TaskSvnRevision,"commit_source_object"=>GraphRelationKind.CommitSourceObject,_=>GraphRelationKind.CommitSvnRevision };
    private static GraphProvenance ParseProvenance(string value)=>value switch { "default"=>GraphProvenance.Default,"evidence"=>GraphProvenance.Evidence,_=>GraphProvenance.Manual };
    private sealed record TaskMetadata(int Priority, string Intent, string ExpectedResult);
    private static string Require(string? value,string name)=>string.IsNullOrWhiteSpace(value)?throw new ArgumentException("A value is required.",name):value;

    private static string UpdatedMetadataJson(GraphTask task)
    {
        var metadata = string.IsNullOrWhiteSpace(task.MetadataJson)
            ? new JsonObject()
            : JsonNode.Parse(task.MetadataJson) as JsonObject ?? new JsonObject();
        metadata["priority"] = task.Priority;
        metadata["intent"] = task.Intent;
        metadata["expectedResult"] = task.ExpectedResult;
        return metadata.ToJsonString();
    }
}
