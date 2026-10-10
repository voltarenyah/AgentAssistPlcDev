using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Workbench.EngineeringGraph;

public sealed class EngineeringGraphService
{
    private readonly EngineeringGraphStore _store;
    private readonly string _workbenchId;
    private readonly Func<string, bool> _worktreeExists;
    private readonly bool _worktreeRegistryKnown;

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

    /// <summary>The property names the inspection ingest owns on a source-object node, as a SQL LIKE
    /// pattern. One constant, so the writer that excludes them and the writer that owns them cannot
    /// drift apart.</summary>
    private const string InspectionPrefixPattern = SourceObjectInspectionPropertyNames.Prefix + "%";

    public EngineeringGraphService(EngineeringGraphStore store, string workbenchId,
        Func<string, bool>? worktreeExists = null)
    {
        _store = store;
        _workbenchId = Require(workbenchId, nameof(workbenchId));
        _worktreeRegistryKnown = worktreeExists is not null;
        _worktreeExists = worktreeExists ?? (_ => false);
    }
    public string WorkbenchId() => _workbenchId;

    public GraphTask CreateTask(string taskId, GraphTaskScopeKind scope, string? worktreeId, string title, GraphTaskType type,
        GraphTaskStatus status = GraphTaskStatus.Todo, string? description = null, int priority = 0,
        string intent = "", string expectedResult = "", string? deviceId = null,
        GraphTaskTargetKind targetKind = GraphTaskTargetKind.Device)
    {
        taskId = Require(taskId, nameof(taskId)); title = Require(title, nameof(title));
        if (scope == GraphTaskScopeKind.Worktree && string.IsNullOrWhiteSpace(worktreeId))
            throw new EngineeringGraphConstraintException("A worktree-scoped task requires a worktree ID.");
        // A hardware task is the one target that resolves without a PLC device (ADR-0007); every other
        // worktree task still needs one, and that rejection code is part of the contract.
        if (scope == GraphTaskScopeKind.Worktree && targetKind == GraphTaskTargetKind.Device && string.IsNullOrWhiteSpace(deviceId))
            throw new EngineeringGraphConstraintException("A worktree-scoped task requires one PLC device.", "TASK_DEVICE_REQUIRED");
        if (worktreeId is not null && !_worktreeExists(worktreeId))
            throw new EngineeringGraphConstraintException($"Worktree '{worktreeId}' is not registered in the current Workbench.");
        if (scope == GraphTaskScopeKind.Project && worktreeId is not null)
            throw new EngineeringGraphConstraintException("A project-scoped task cannot have a worktree ID.");
        if (scope != GraphTaskScopeKind.Worktree && deviceId is not null)
            throw new EngineeringGraphConstraintException("Only worktree tasks can bind a PLC device.");
        if (scope != GraphTaskScopeKind.Worktree && targetKind != GraphTaskTargetKind.Device)
            throw new EngineeringGraphConstraintException("Only worktree tasks can target hardware configuration.");
        var now = DateTimeOffset.UtcNow;
        intent = Require(intent, nameof(intent));
        expectedResult = Require(expectedResult, nameof(expectedResult));
        var task = new GraphTask(taskId, _workbenchId, scope, worktreeId, title, type, status, description,
            JsonSerializer.Serialize(new { priority, intent, expectedResult }), now, now, priority, intent, expectedResult, deviceId, targetKind);
        using var tx = _store.Connection.BeginTransaction();
        Execute(tx, """
            INSERT INTO tasks (task_id, workbench_id, scope_kind, worktree_id, device_id, target_kind, type, status, title, description, metadata_json, created_utc, updated_utc)
            VALUES ($id,$wb,$scope,$wt,$device,$target,$type,$status,$title,$description,$metadata,$created,$updated);
            INSERT INTO graph_entities (entity_kind, entity_id, workbench_id, worktree_id, device_id) VALUES ('task',$id,$wb,$wt,$device);
            """, ("$id", task.TaskId), ("$wb", _workbenchId), ("$scope", task.ScopeKind.ToString().ToLowerInvariant()),
            ("$wt", task.WorktreeId), ("$device", task.DeviceId), ("$target", TargetKind(task.TargetKind)),
            ("$type", task.Type.ToString().ToLowerInvariant()), ("$status", task.Status.ToString().ToLowerInvariant()),
            ("$title", task.Title), ("$description", task.Description), ("$metadata", task.MetadataJson),
            ("$created", now.ToString("O")), ("$updated", now.ToString("O")));
        tx.Commit();
        return task;
    }

    public TaskSourceStage StageSourceObject(string taskId, string sourceObjectId, string? baselineEvidenceJson = null)
    {
        var task = FindTask(taskId) ?? throw new EngineeringGraphConstraintException("Task was not found.", "TASK_NOT_FOUND");
        if (task.ScopeKind != GraphTaskScopeKind.Worktree || task.TargetKind != GraphTaskTargetKind.Device || string.IsNullOrWhiteSpace(task.DeviceId))
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

    /// <summary>Active stages across one worktree, with the owning task's title. Callers use it to
    /// show who owns a source object before taking it over (the unique active-owner index enforces
    /// one owner per object; this read makes that owner visible instead of only reportable).</summary>
    public IReadOnlyList<WorktreeSourceStage> ListWorktreeActiveStages(string worktreeId)
    {
        using var command = _store.Connection.CreateCommand(); command.CommandText = "SELECT stage.task_id,task.title,stage.source_object_id,stage.device_id,stage.baseline_evidence_json,stage.staged_utc FROM task_source_stages stage JOIN tasks task ON task.task_id=stage.task_id WHERE stage.worktree_id=$worktree AND stage.released_utc IS NULL ORDER BY stage.source_object_id;"; command.Parameters.AddWithValue("$worktree", worktreeId);
        using var reader = command.ExecuteReader(); var result = new List<WorktreeSourceStage>();
        while (reader.Read()) result.Add(new WorktreeSourceStage(
            new TaskSourceStage(reader.GetString(0), worktreeId, reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5))),
            reader.GetString(1)));
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
            updated.ScopeKind != current.ScopeKind || updated.WorktreeId != current.WorktreeId || updated.DeviceId != current.DeviceId ||
            updated.TargetKind != current.TargetKind)
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
        // The task's node row, its property rows and every edge that references it go first: the graph
        // has no foreign key on graph_edges, so leaving them behind left each conversation that related
        // to the task naming a task FindTask can no longer resolve. One relation per conversation made
        // that a latent dangling reference; a conversation with several relations multiplies it
        // (ADR-0014, closely-related cleanup).
        RemoveEntityWithin(tx, Kind(GraphEntityKind.Task), taskId);
        using var command = _store.Connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM task_source_stages WHERE task_id=$id; DELETE FROM tasks WHERE task_id=$id AND workbench_id=$wb;";
        command.Parameters.AddWithValue("$id", taskId); command.Parameters.AddWithValue("$wb", _workbenchId);
        var removed = command.ExecuteNonQuery() > 0;
        tx.Commit();
        return removed;
    }

    public void RegisterEntity(GraphEntity entity)
    {
        ValidateEntity(entity);
        ExecuteNonQuery("INSERT OR REPLACE INTO graph_entities (entity_kind,entity_id,workbench_id,worktree_id,device_id,external_ref) VALUES ($kind,$id,$wb,$wt,$device,$ref)",
            ("$kind", Kind(entity.Kind)), ("$id", entity.EntityId), ("$wb", entity.WorkbenchId), ("$wt", entity.WorktreeId),
            ("$device", entity.DeviceId), ("$ref", entity.ExternalRef));
    }

    /// <summary>
    /// Registers a whole set of entities in one transaction. A device manifest holds more than a
    /// thousand objects, and one autocommit per object made registering it against the graph take
    /// seconds — the dominant cost of staging a single source object.
    /// </summary>
    public void RegisterEntities(IEnumerable<GraphEntity> entities)
    {
        var pending = entities as IReadOnlyCollection<GraphEntity> ?? entities.ToArray();
        if (pending.Count == 0) return;
        foreach (var entity in pending) ValidateEntity(entity);
        using var transaction = _store.Connection.BeginTransaction();
        foreach (var entity in pending)
        {
            Execute(transaction, "INSERT OR REPLACE INTO graph_entities (entity_kind,entity_id,workbench_id,worktree_id,device_id,external_ref) VALUES ($kind,$id,$wb,$wt,$device,$ref)",
                ("$kind", Kind(entity.Kind)), ("$id", entity.EntityId), ("$wb", entity.WorkbenchId), ("$wt", entity.WorktreeId),
                ("$device", entity.DeviceId), ("$ref", entity.ExternalRef));
        }
        transaction.Commit();
    }

    private void ValidateEntity(GraphEntity entity)
    {
        if (entity.Kind == GraphEntityKind.Task) throw new EngineeringGraphConstraintException("Tasks must be created with CreateTask.");
        if (entity.WorkbenchId != _workbenchId) throw new EngineeringGraphConstraintException("Entity belongs to another Workbench.");
        if (entity.WorktreeId is not null && !_worktreeExists(entity.WorktreeId))
            throw new EngineeringGraphConstraintException($"Worktree '{entity.WorktreeId}' is not registered in the current Workbench.");
        Require(entity.EntityId, nameof(entity.EntityId));
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
        if (fromKind == GraphEntityKind.Task && from.DeviceId is not null && to.DeviceId is not null && from.DeviceId != to.DeviceId)
            throw new EngineeringGraphConstraintException("A task can only link records from its bound PLC device.", "TASK_DEVICE_MISMATCH");
        if (isPrimary && relation is not (GraphRelationKind.TaskCommit or GraphRelationKind.TaskSession))
            throw new EngineeringGraphConstraintException("Only task-to-commit and task-to-session relationships may be primary.");
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
        if (isPrimary && targetKind is not (GraphEntityKind.GitCommit or GraphEntityKind.Session))
            throw new EngineeringGraphConstraintException("Only task-to-commit and task-to-session relationships may be primary.");
        if (task.ScopeKind == GraphTaskScopeKind.Worktree && task.WorktreeId != target.WorktreeId)
            throw new EngineeringGraphConstraintException("A worktree-scoped task can only link within its Worktree.");
        if (task.DeviceId is not null && target.DeviceId is not null && task.DeviceId != target.DeviceId)
            throw new EngineeringGraphConstraintException("A task can only link records from its bound PLC device.", "TASK_DEVICE_MISMATCH");
        // A conversation holds several relations (ADR-0014), so a session target is added rather than
        // replaced, and the route keeps its "this is the primary now" meaning through PromoteSessionTask.
        // Every other target keeps the pair-replacement behaviour below.
        if (targetKind == GraphEntityKind.Session)
        {
            var added = AddSessionTask(targetId, taskId, provenance);
            return isPrimary ? PromoteSessionTask(targetId, taskId) : added;
        }
        using var tx = _store.Connection.BeginTransaction();
        Execute(tx, "DELETE FROM graph_edges WHERE from_kind='task' AND from_id=$task AND to_kind=$kind AND to_id=$target;",
            ("$task", taskId), ("$kind", Kind(targetKind)), ("$target", targetId));
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
        if (replacement.DeviceId is not null && target.DeviceId is not null && replacement.DeviceId != target.DeviceId)
            throw new EngineeringGraphConstraintException("A task can only link records from its bound PLC device.", "TASK_DEVICE_MISMATCH");
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
    /// <summary>
    /// Removes a node, its property rows and every edge that references it, in one transaction. The
    /// graph has no foreign key on <c>graph_edges</c>, and the property table deliberately carries no
    /// cascading delete, so both are removed here. Returns the number of property rows removed.
    /// </summary>
    public int RemoveEntity(GraphEntityKind kind, string entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return 0;
        using var transaction = _store.Connection.BeginTransaction();
        var properties = RemoveEntityWithin(transaction, Kind(kind), entityId);
        transaction.Commit();
        return properties;
    }

    private int RemoveEntityWithin(SqliteTransaction transaction, string kind, string entityId)
    {
        var properties = Execute(transaction, "DELETE FROM graph_entity_properties WHERE entity_kind=$kind AND entity_id=$id", ("$kind", kind), ("$id", entityId));
        Execute(transaction, "DELETE FROM graph_edges WHERE from_kind=$kind AND from_id=$id OR to_kind=$kind AND to_id=$id", ("$kind", kind), ("$id", entityId));
        Execute(transaction, "DELETE FROM graph_entities WHERE entity_kind=$kind AND entity_id=$id", ("$kind", kind), ("$id", entityId));
        return properties;
    }

    // ---------------------------------------------------------------------------------------------
    // Node properties (schema v6). Facts are read without a join and replaced as a set per node, so a
    // fact the new ingest no longer produces disappears instead of surviving as a stale row.
    // ---------------------------------------------------------------------------------------------

    /// <summary>One node's property rows, ordered by name.</summary>
    public IReadOnlyList<GraphProperty> GetProperties(GraphEntityKind kind, string entityId)
    {
        Require(entityId, nameof(entityId));
        using var command = _store.Connection.CreateCommand();
        command.CommandText = "SELECT name,value_kind,value_text,value_number,value_flag,value_timestamp,value_json,source FROM graph_entity_properties WHERE entity_kind=$kind AND entity_id=$id ORDER BY name;";
        command.Parameters.AddWithValue("$kind", Kind(kind));
        command.Parameters.AddWithValue("$id", entityId);
        var result = new List<GraphProperty>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(ReadProperty(reader, 0));
        return result;
    }

    /// <summary>
    /// Every property of every node one device owns — the device node and its source objects — in a
    /// single statement, keyed by entity id. The join walks graph_entities by <c>device_id</c> and the
    /// property side is served by the covering index on (entity_kind, entity_id), so a device page
    /// never reads its ~14k rows one node at a time.
    /// </summary>
    /// <remarks>
    /// The per-object parsed inspection content is deliberately excluded: it is large (tens of
    /// megabytes for a real device) and no device route reads it, so a device read would pay for it
    /// without using it. <see cref="GetInspectionFacts"/> and
    /// <see cref="GetProperties(GraphEntityKind, string)"/> are the readers for it.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<GraphProperty>> GetDeviceProperties(string deviceId)
    {
        Require(deviceId, nameof(deviceId));
        using var command = _store.Connection.CreateCommand();
        command.CommandText = """
            SELECT entity.entity_id, property.name, property.value_kind, property.value_text, property.value_number,
                   property.value_flag, property.value_timestamp, property.value_json, property.source
            FROM graph_entities entity
            JOIN graph_entity_properties property
              ON property.entity_kind = entity.entity_kind AND property.entity_id = entity.entity_id
            WHERE entity.workbench_id = $wb AND entity.device_id = $device
              AND property.name NOT LIKE $inspection
            ORDER BY entity.entity_id, property.name;
            """;
        command.Parameters.AddWithValue("$wb", _workbenchId);
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$inspection", InspectionPrefixPattern);
        var result = new Dictionary<string, List<GraphProperty>>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!result.TryGetValue(id, out var properties))
                result[id] = properties = new List<GraphProperty>();
            properties.Add(ReadProperty(reader, 1));
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<GraphProperty>)pair.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// What the projection already knows about each object's parsed content, for its reuse decision:
    /// the stored inspection facts of every node one device owns, without the payload. An object whose
    /// exported content has not moved is not parsed again, so a re-projection that runs for an
    /// unrelated reason costs no XML read (ADR-0011 Phase 5).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<GraphProperty>> GetInspectionFacts(string deviceId)
    {
        Require(deviceId, nameof(deviceId));
        using var command = _store.Connection.CreateCommand();
        command.CommandText = """
            SELECT entity.entity_id, property.name, property.value_kind, property.value_text, property.value_number,
                   property.value_flag, property.value_timestamp, property.value_json, property.source
            FROM graph_entities entity
            JOIN graph_entity_properties property
              ON property.entity_kind = entity.entity_kind AND property.entity_id = entity.entity_id
            WHERE entity.workbench_id = $wb AND entity.device_id = $device
              AND property.name LIKE $inspection AND property.name <> $payload
            ORDER BY entity.entity_id, property.name;
            """;
        command.Parameters.AddWithValue("$wb", _workbenchId);
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$inspection", InspectionPrefixPattern);
        command.Parameters.AddWithValue("$payload", SourceObjectInspectionPropertyNames.Payload);
        var result = new Dictionary<string, List<GraphProperty>>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!result.TryGetValue(id, out var properties))
                result[id] = properties = new List<GraphProperty>();
            properties.Add(ReadProperty(reader, 1));
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<GraphProperty>)pair.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Writes the parsed content of the objects a projection re-derived, in one transaction, as a set
    /// that replaces only those nodes' inspection facts: a fact the new set no longer produces is
    /// deleted, and the objects whose stored content the projection reused are not touched at all.
    /// The manifest facts on the same nodes belong to <see cref="ReplaceDeviceProperties"/> and are
    /// never read or written here.
    /// </summary>
    public GraphPropertyWriteResult ReplaceInspectionProperties(
        string deviceId,
        IReadOnlyList<GraphNodePropertySet> nodes)
    {
        Require(deviceId, nameof(deviceId));
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0) return new GraphPropertyWriteResult(0, 0, 0, 0);
        foreach (var node in nodes)
            ValidateProjectedNode(deviceId, node.Entity);

        using var transaction = _store.Connection.BeginTransaction();
        var inserted = 0;
        var updated = 0;
        var deleted = 0;
        foreach (var node in nodes)
        {
            var kind = Kind(node.Entity.Kind);
            var stored = ReadPropertySetWithin(transaction, kind, node.Entity.EntityId, InspectionPrefixPattern);
            var write = ReplacePropertySet(transaction, kind, node.Entity.EntityId, node.Properties, stored);
            inserted += write.Inserted;
            updated += write.Updated;
            deleted += write.Deleted;
        }

        transaction.Commit();
        return new GraphPropertyWriteResult(inserted, updated, deleted, 0);
    }

    /// <summary>
    /// Replaces one node's property set with <paramref name="properties"/> in one transaction: a
    /// property the new set omits is deleted. Returns how many rows changed (inserted, updated or
    /// deleted). The node's own row is not written here — the caller registers the node — and a set
    /// that names one property twice is rejected, rolling the whole set back.
    /// </summary>
    public int ReplaceProperties(GraphEntityKind kind, string entityId, IReadOnlyList<GraphProperty> properties)
    {
        Require(entityId, nameof(entityId));
        ArgumentNullException.ThrowIfNull(properties);
        using var transaction = _store.Connection.BeginTransaction();
        var write = ReplacePropertySet(transaction, Kind(kind), entityId, properties, ReadPropertySet(transaction, Kind(kind), entityId));
        transaction.Commit();
        return write.Total;
    }

    /// <summary>
    /// Writes one device's projected facts — its own node plus its source objects — in a single
    /// transaction, and returns how many property rows changed. Each node's set replaces what the node
    /// held; a source object the new set no longer names loses its node, its properties and every edge
    /// that referenced it. Nothing is read from the filesystem here: the caller resolves the manifest
    /// first, so the transaction stays short (ADR-0012).
    /// </summary>
    public GraphPropertyWriteResult ReplaceDeviceProperties(string deviceId, IReadOnlyList<GraphNodePropertySet> nodes)
    {
        Require(deviceId, nameof(deviceId));
        ArgumentNullException.ThrowIfNull(nodes);
        foreach (var node in nodes)
            ValidateProjectedNode(deviceId, node.Entity);

        using var transaction = _store.Connection.BeginTransaction();
        var stored = ReadDevicePropertySets(transaction, deviceId);
        var existingSourceObjects = ReadDeviceSourceObjectIds(transaction, deviceId);
        var inserted = 0;
        var updated = 0;
        var deleted = 0;
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var kind = Kind(node.Entity.Kind);
            UpsertEntityWithin(transaction, kind, node.Entity);
            if (node.Entity.Kind == GraphEntityKind.SourceObject)
                kept.Add(node.Entity.EntityId);
            stored.TryGetValue((kind, node.Entity.EntityId), out var current);
            var write = ReplacePropertySet(transaction, kind, node.Entity.EntityId, node.Properties, current);
            inserted += write.Inserted;
            updated += write.Updated;
            deleted += write.Deleted;
        }

        var removed = 0;
        foreach (var entityId in existingSourceObjects.Where(id => !kept.Contains(id)))
        {
            deleted += RemoveEntityWithin(transaction, Kind(GraphEntityKind.SourceObject), entityId);
            removed++;
        }

        transaction.Commit();
        return new GraphPropertyWriteResult(inserted, updated, deleted, removed);
    }

    private sealed record PropertySetWrite(int Inserted, int Updated, int Deleted)
    {
        public int Total => Inserted + Updated + Deleted;
    }

    /// <summary>Replaces one node's set against what is already stored. Duplicate names are detected
    /// while rows are written, so a rejected set rolls the transaction back whole.</summary>
    private PropertySetWrite ReplacePropertySet(SqliteTransaction transaction, string kind, string entityId,
        IReadOnlyList<GraphProperty> desired, Dictionary<string, GraphProperty>? stored)
    {
        var inserted = 0;
        var updated = 0;
        var deleted = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in desired)
        {
            if (!names.Add(property.Name))
                throw new EngineeringGraphConstraintException($"Property '{property.Name}' is set twice for one node; a property set holds one value per name.");
            if (stored is not null && stored.TryGetValue(property.Name, out var existing))
            {
                if (existing.Equals(property)) continue;
                UpsertProperty(transaction, kind, entityId, property);
                updated++;
                continue;
            }
            UpsertProperty(transaction, kind, entityId, property);
            inserted++;
        }

        if (stored is not null)
        {
            foreach (var name in stored.Keys)
            {
                if (names.Contains(name)) continue;
                Execute(transaction, "DELETE FROM graph_entity_properties WHERE entity_kind=$kind AND entity_id=$id AND name=$name",
                    ("$kind", kind), ("$id", entityId), ("$name", name));
                deleted++;
            }
        }

        return new PropertySetWrite(inserted, updated, deleted);
    }

    private void UpsertProperty(SqliteTransaction transaction, string kind, string entityId, GraphProperty property) =>
        Execute(transaction, """
            INSERT INTO graph_entity_properties (entity_kind, entity_id, name, value_kind, value_text, value_number, value_flag, value_timestamp, value_json, source)
            VALUES ($kind,$id,$name,$valueKind,$text,$number,$flag,$timestamp,$json,$source)
            ON CONFLICT(entity_kind, entity_id, name) DO UPDATE SET
                value_kind=excluded.value_kind, value_text=excluded.value_text, value_number=excluded.value_number,
                value_flag=excluded.value_flag, value_timestamp=excluded.value_timestamp, value_json=excluded.value_json,
                source=excluded.source;
            """,
            ("$kind", kind), ("$id", entityId), ("$name", property.Name), ("$valueKind", ValueKind(property.Kind)),
            ("$text", property.Text), ("$number", property.Number),
            ("$flag", property.Flag is null ? null : property.Flag.Value ? 1 : 0),
            ("$timestamp", property.Timestamp?.ToString("O")), ("$json", property.Json), ("$source", property.Source));

    /// <summary>Registers a node without replacing its row: INSERT OR REPLACE deletes the replaced row,
    /// and the property table's survival must not depend on nothing cascading from it (ADR-0011).</summary>
    private void UpsertEntityWithin(SqliteTransaction transaction, string kind, GraphEntity entity) =>
        Execute(transaction, """
            INSERT INTO graph_entities (entity_kind, entity_id, workbench_id, worktree_id, device_id, external_ref)
            VALUES ($kind,$id,$wb,$wt,$device,$ref)
            ON CONFLICT(entity_kind, entity_id) DO UPDATE SET
                workbench_id=excluded.workbench_id, worktree_id=excluded.worktree_id,
                device_id=excluded.device_id, external_ref=excluded.external_ref;
            """,
            ("$kind", kind), ("$id", entity.EntityId), ("$wb", entity.WorkbenchId), ("$wt", entity.WorktreeId),
            ("$device", entity.DeviceId), ("$ref", entity.ExternalRef));

    private void ValidateProjectedNode(string deviceId, GraphEntity entity)
    {
        if (entity.Kind == GraphEntityKind.Task)
            throw new EngineeringGraphConstraintException("Tasks must be created with CreateTask.");
        if (entity.Kind is not (GraphEntityKind.Device or GraphEntityKind.SourceObject))
            throw new EngineeringGraphConstraintException("A device projection writes device and source_object nodes only.");
        if (entity.WorkbenchId != _workbenchId)
            throw new EngineeringGraphConstraintException("Entity belongs to another Workbench.");
        if (!string.Equals(entity.DeviceId, deviceId, StringComparison.Ordinal))
            throw new EngineeringGraphConstraintException("A projected node must belong to the device being projected.");
        Require(entity.EntityId, nameof(entity.EntityId));
    }

    /// <summary>
    /// The manifest facts of one device's nodes, for the write path's replace-as-a-set diff. The
    /// per-object inspection facts are excluded on purpose: they are a second, disjoint property set
    /// on the same nodes, written by <see cref="ReplaceInspectionProperties"/>, so this write must
    /// neither load them (tens of megabytes) nor treat them as facts it no longer produces.
    /// </summary>
    private Dictionary<(string Kind, string Id), Dictionary<string, GraphProperty>> ReadDevicePropertySets(SqliteTransaction transaction, string deviceId)
    {
        using var command = _store.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT entity.entity_kind, entity.entity_id, property.name, property.value_kind, property.value_text,
                   property.value_number, property.value_flag, property.value_timestamp, property.value_json, property.source
            FROM graph_entities entity
            JOIN graph_entity_properties property
              ON property.entity_kind = entity.entity_kind AND property.entity_id = entity.entity_id
            WHERE entity.workbench_id = $wb AND entity.device_id = $device
              AND property.name NOT LIKE $inspection;
            """;
        command.Parameters.AddWithValue("$wb", _workbenchId);
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$inspection", InspectionPrefixPattern);
        var result = new Dictionary<(string, string), Dictionary<string, GraphProperty>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!result.TryGetValue(key, out var properties))
                result[key] = properties = new Dictionary<string, GraphProperty>(StringComparer.Ordinal);
            var property = ReadProperty(reader, 2);
            properties[property.Name] = property;
        }
        return result;
    }

    /// <summary>The stored property set of one node, restricted to the names a pattern selects, so a
    /// writer can own a name space on a node another writer also writes.</summary>
    private Dictionary<string, GraphProperty>? ReadPropertySetWithin(
        SqliteTransaction transaction, string kind, string entityId, string namePattern)
    {
        using var command = _store.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name,value_kind,value_text,value_number,value_flag,value_timestamp,value_json,source FROM graph_entity_properties WHERE entity_kind=$kind AND entity_id=$id AND name LIKE $pattern;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", entityId);
        command.Parameters.AddWithValue("$pattern", namePattern);
        Dictionary<string, GraphProperty>? result = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result ??= new Dictionary<string, GraphProperty>(StringComparer.Ordinal);
            var property = ReadProperty(reader, 0);
            result[property.Name] = property;
        }
        return result;
    }

    private Dictionary<string, GraphProperty>? ReadPropertySet(SqliteTransaction transaction, string kind, string entityId)
    {
        using var command = _store.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name,value_kind,value_text,value_number,value_flag,value_timestamp,value_json,source FROM graph_entity_properties WHERE entity_kind=$kind AND entity_id=$id;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$id", entityId);
        Dictionary<string, GraphProperty>? result = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result ??= new Dictionary<string, GraphProperty>(StringComparer.Ordinal);
            var property = ReadProperty(reader, 0);
            result[property.Name] = property;
        }
        return result;
    }

    private List<string> ReadDeviceSourceObjectIds(SqliteTransaction transaction, string deviceId)
    {
        using var command = _store.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT entity_id FROM graph_entities WHERE entity_kind='source_object' AND workbench_id=$wb AND device_id=$device;";
        command.Parameters.AddWithValue("$wb", _workbenchId);
        command.Parameters.AddWithValue("$device", deviceId);
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Reads the nine property columns starting at <paramref name="offset"/> (name, kind,
    /// text, number, flag, timestamp, json, source).</summary>
    private static GraphProperty ReadProperty(SqliteDataReader reader, int offset) => new(
        reader.GetString(offset),
        ParseValueKind(reader.GetString(offset + 1)),
        reader.GetString(offset + 7),
        Text: reader.IsDBNull(offset + 2) ? null : reader.GetString(offset + 2),
        Number: reader.IsDBNull(offset + 3) ? null : reader.GetDouble(offset + 3),
        Flag: reader.IsDBNull(offset + 4) ? null : reader.GetInt32(offset + 4) != 0,
        Timestamp: reader.IsDBNull(offset + 5) ? null : DateTimeOffset.Parse(reader.GetString(offset + 5)),
        Json: reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6));

    private static string ValueKind(GraphPropertyValueKind kind) => kind switch
    {
        GraphPropertyValueKind.Number => "number",
        GraphPropertyValueKind.Flag => "flag",
        GraphPropertyValueKind.Timestamp => "timestamp",
        GraphPropertyValueKind.Json => "json",
        _ => "text",
    };

    private static GraphPropertyValueKind ParseValueKind(string value) => value switch
    {
        "number" => GraphPropertyValueKind.Number,
        "flag" => GraphPropertyValueKind.Flag,
        "timestamp" => GraphPropertyValueKind.Timestamp,
        "json" => GraphPropertyValueKind.Json,
        _ => GraphPropertyValueKind.Text,
    };

    // ---------------------------------------------------------------------------------------------
    // Projection freshness and reconciliation (schema v6, ADR-0012). Invalidation is one statement per
    // write point and no file I/O; the worktree cleanup is one transaction that removes a deleted
    // worktree's nodes, their property rows and every edge referencing them, while keeping the facts a
    // worktree that still exists owns. Both stay inside the store's existing concurrency model: no
    // statement here reads a file.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Marks one device's projection as needing a re-projection (ADR-0012 item 2). One statement, so a
    /// write point may call it inline; a device that was never projected has no flag to set and is
    /// projected on demand by the next read. Returns whether a projected device was flagged.
    /// </summary>
    public bool InvalidateDeviceProjection(string deviceId)
    {
        Require(deviceId, nameof(deviceId));
        return ExecuteNonQuery(
            "UPDATE graph_entity_properties SET value_flag = 1 WHERE entity_kind = 'device' AND entity_id = $id AND name = $name;",
            ("$id", deviceId), ("$name", DevicePropertyNames.ProjectionInvalidated)) > 0;
    }

    /// <summary>
    /// The same for every device whose stored facts came from one worktree: a source apply, a commit or
    /// a branch switch in that worktree invalidates exactly the facts it produced, and leaves the facts
    /// of another worktree alone.
    /// </summary>
    /// <remarks>
    /// The worktree's hardware facts are flagged too: a branch switch, a checkout or a raw gateway
    /// commit moves the whole worktree — the <c>hardware/</c> subtree included — so the hardware routes
    /// re-project before serving. The return value stays the number of device projections flagged.
    /// </remarks>
    public int InvalidateWorktreeProjections(string worktreeId)
    {
        Require(worktreeId, nameof(worktreeId));
        var devices = ExecuteNonQuery("""
            UPDATE graph_entity_properties SET value_flag = 1
            WHERE entity_kind = 'device' AND name = $invalidated
              AND entity_id IN (
                  SELECT entity.entity_id
                  FROM graph_entities entity
                  JOIN graph_entity_properties origin
                    ON origin.entity_kind = entity.entity_kind AND origin.entity_id = entity.entity_id
                  WHERE entity.workbench_id = $wb AND entity.entity_kind = 'device'
                    AND origin.name = $origin AND origin.value_text = $wt);
            """,
            ("$invalidated", DevicePropertyNames.ProjectionInvalidated),
            ("$wb", _workbenchId),
            ("$origin", DevicePropertyNames.WorktreeId),
            ("$wt", worktreeId));
        InvalidateHardwareProjection(worktreeId);
        return devices;
    }

    /// <summary>
    /// Marks one worktree's hardware facts as needing a re-projection. One statement, and a worktree
    /// whose hardware subtree was never projected has no flag to set — the next hardware route projects
    /// it on demand. Returns whether a projected hardware subtree was flagged.
    /// </summary>
    public bool InvalidateHardwareProjection(string worktreeId)
    {
        Require(worktreeId, nameof(worktreeId));
        return ExecuteNonQuery(
            "UPDATE graph_entity_properties SET value_flag = 1 WHERE entity_kind = 'worktree' AND entity_id = $id AND name = $name;",
            ("$id", worktreeId), ("$name", HardwarePropertyNames.Invalidated)) > 0;
    }

    /// <summary>
    /// Writes one worktree's hardware facts — its <see cref="GraphEntityKind.Worktree"/> node and the
    /// property set that describes the hardware/AML subtree — in one transaction. The node is
    /// registered without replacing an existing row, and the set replaces what the node held, so a
    /// fact the new ingest no longer produces disappears. Nothing is read from the filesystem here.
    /// </summary>
    public int ReplaceHardwareFacts(string worktreeId, IReadOnlyList<GraphProperty> properties)
    {
        Require(worktreeId, nameof(worktreeId));
        ArgumentNullException.ThrowIfNull(properties);
        if (!_worktreeExists(worktreeId))
            throw new EngineeringGraphConstraintException($"Worktree '{worktreeId}' is not registered in the current Workbench.");
        foreach (var property in properties)
        {
            if (!property.Name.StartsWith("hardware.", StringComparison.Ordinal))
                throw new EngineeringGraphConstraintException(
                    $"Property '{property.Name}' is not a hardware fact; the worktree node holds the hardware projection only.");
        }

        using var transaction = _store.Connection.BeginTransaction();
        Execute(transaction, """
            INSERT INTO graph_entities (entity_kind, entity_id, workbench_id, worktree_id, device_id, external_ref)
            VALUES ('worktree',$id,$wb,$wt,NULL,NULL)
            ON CONFLICT(entity_kind, entity_id) DO UPDATE SET
                workbench_id=excluded.workbench_id, worktree_id=excluded.worktree_id;
            """,
            ("$id", worktreeId), ("$wb", _workbenchId), ("$wt", worktreeId));
        var write = ReplacePropertySet(transaction, Kind(GraphEntityKind.Worktree), worktreeId, properties,
            ReadPropertySet(transaction, Kind(GraphEntityKind.Worktree), worktreeId));
        transaction.Commit();
        return write.Total;
    }

    /// <summary>
    /// Removes the facts of every worktree the current Workbench no longer registers (ADR-0012 item 4,
    /// AC-007): its nodes, their property rows and every edge that references them, in one transaction.
    /// A device id another worktree still owns keeps its facts — the deleted worktree is only removed
    /// from the device's owning set (<see cref="DevicePropertyNames.ProjectionWorktrees"/>) and the
    /// facts go with the last owner, so a shared device id is never "last writer wins".
    /// </summary>
    /// <remarks>
    /// Rows in <c>tasks</c> and <c>task_source_stages</c> are deliberately untouched: AC-007 scopes the
    /// removal to nodes, their properties and the edges that reference them. A projected device whose
    /// stored facts predate the owning-set property has no set to preserve, so its node is removed with
    /// its worktree and re-projected from its next selection.
    /// </remarks>
    public WorktreeFactCleanupResult RemoveUnregisteredWorktreeFacts()
    {
        if (!_worktreeRegistryKnown)
            throw new EngineeringGraphConstraintException(
                "Removing a deleted worktree's facts requires the Workbench's registered worktrees.",
                "GRAPH_WORKTREE_REGISTRY_REQUIRED");

        var deleted = ReadWorktreeIds().Where(id => !_worktreeExists(id)).ToArray();
        if (deleted.Length == 0) return WorktreeFactCleanupResult.Empty;
        var deletedSet = deleted.ToHashSet(StringComparer.Ordinal);

        using var transaction = _store.Connection.BeginTransaction();
        var nodesBefore = CountRowsWithin(transaction, "graph_entities");
        var propertiesBefore = CountRowsWithin(transaction, "graph_entity_properties");
        var edgesBefore = CountRowsWithin(transaction, "graph_edges");

        var devicesRemoved = new List<string>();
        var devicesRetained = new List<string>();
        foreach (var (deviceId, worktrees) in ReadProjectionWorktrees(transaction))
        {
            if (!worktrees.Any(deletedSet.Contains)) continue;
            var remaining = worktrees.Where(id => !deletedSet.Contains(id)).ToArray();
            if (remaining.Length == 0)
            {
                devicesRemoved.Add(deviceId);
                RemoveDeviceWithin(transaction, deviceId);
                continue;
            }

            devicesRetained.Add(deviceId);
            RetainDeviceWithin(transaction, deviceId, remaining, deletedSet);
        }

        // Everything still attributed to a deleted worktree: its commit, SVN revision, session and task
        // nodes, plus any source-object anchor a projection did not own. The devices kept above were
        // re-attributed to a worktree that still exists, so they are not selected here.
        foreach (var (kind, entityId) in ReadEntitiesOfWorktrees(transaction, deleted))
            RemoveEntityWithin(transaction, kind, entityId);

        var result = new WorktreeFactCleanupResult(
            deleted,
            nodesBefore - CountRowsWithin(transaction, "graph_entities"),
            propertiesBefore - CountRowsWithin(transaction, "graph_entity_properties"),
            edgesBefore - CountRowsWithin(transaction, "graph_edges"),
            devicesRemoved,
            devicesRetained);
        transaction.Commit();
        return result;
    }

    /// <summary>The device node and its source objects, with their properties and edges, in the
    /// caller's transaction.</summary>
    private void RemoveDeviceWithin(SqliteTransaction transaction, string deviceId)
    {
        foreach (var entityId in ReadDeviceSourceObjectIds(transaction, deviceId))
            RemoveEntityWithin(transaction, Kind(GraphEntityKind.SourceObject), entityId);
        RemoveEntityWithin(transaction, Kind(GraphEntityKind.Device), deviceId);
    }

    /// <summary>
    /// Keeps a device another worktree still owns: rewrites its owning set without the deleted
    /// worktrees, and moves the device's own nodes — its device node and its projected source objects —
    /// onto a worktree that exists. Only those nodes move: a task, conversation or commit of the deleted
    /// worktree carries the same device id but belongs to the worktree, so it is removed with it rather
    /// than re-attributed to a worktree that never owned it. The device's <c>identity.worktreeId</c>
    /// property is left alone on purpose — it names whose exported content the stored facts are, so a
    /// read for the surviving worktree re-projects instead of serving content from the deleted one.
    /// </summary>
    private void RetainDeviceWithin(
        SqliteTransaction transaction,
        string deviceId,
        IReadOnlyList<string> remaining,
        IReadOnlySet<string> deleted)
    {
        Execute(transaction,
            "UPDATE graph_entity_properties SET value_json = $json WHERE entity_kind = 'device' AND entity_id = $id AND name = $name;",
            ("$json", JsonSerializer.Serialize(remaining)), ("$id", deviceId),
            ("$name", DevicePropertyNames.ProjectionWorktrees));
        ExecuteAll(transaction, $"""
            UPDATE graph_entities SET worktree_id = $wt
            WHERE workbench_id = $wb AND worktree_id IN ({Placeholders(deleted.ToArray(), "$dead")})
              AND ((entity_kind = 'device' AND entity_id = $id)
                   OR (entity_kind = 'source_object' AND device_id = $id));
            """,
            [("$wt", remaining[0]), ("$wb", _workbenchId), ("$id", deviceId),
                .. deleted.Select((id, index) => ("$dead" + index, (object?)id))]);
    }

    /// <summary>Every worktree id the graph holds a node for in this workbench.</summary>
    private IReadOnlyList<string> ReadWorktreeIds()
    {
        using var command = _store.Connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT worktree_id FROM graph_entities WHERE workbench_id = $wb AND worktree_id IS NOT NULL ORDER BY worktree_id;";
        command.Parameters.AddWithValue("$wb", _workbenchId);
        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Each projected device's owning worktrees, from the <c>projection.worktrees</c> property
    /// the projection writes.</summary>
    private Dictionary<string, IReadOnlyList<string>> ReadProjectionWorktrees(SqliteTransaction transaction)
    {
        using var command = _store.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT property.entity_id, property.value_json
            FROM graph_entity_properties property
            JOIN graph_entities entity
              ON entity.entity_kind = property.entity_kind AND entity.entity_id = property.entity_id
            WHERE entity.workbench_id = $wb AND entity.entity_kind = 'device' AND property.name = $name;
            """;
        command.Parameters.AddWithValue("$wb", _workbenchId);
        command.Parameters.AddWithValue("$name", DevicePropertyNames.ProjectionWorktrees);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.IsDBNull(1)
                ? []
                : EngineeringGraphProjectionService.ParseWorktrees(reader.GetString(1));
        return result;
    }

    /// <summary>Every node the graph attributes to one of <paramref name="worktreeIds"/>, read after the
    /// retained devices have been re-attributed.</summary>
    private IReadOnlyList<(string Kind, string EntityId)> ReadEntitiesOfWorktrees(
        SqliteTransaction transaction,
        IReadOnlyList<string> worktreeIds)
    {
        using var command = _store.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT entity_kind, entity_id FROM graph_entities WHERE workbench_id = $wb AND worktree_id IN ({Placeholders(worktreeIds, "$wt")}) ORDER BY entity_kind, entity_id;";
        command.Parameters.AddWithValue("$wb", _workbenchId);
        for (var index = 0; index < worktreeIds.Count; index++)
            command.Parameters.AddWithValue("$wt" + index, worktreeIds[index]);
        var result = new List<(string, string)>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static string Placeholders(IReadOnlyList<string> values, string prefix) =>
        values.Count == 0 ? "NULL" : string.Join(',', values.Select((_, index) => prefix + index));

    private int CountRowsWithin(SqliteTransaction transaction, string table)
    {
        using var command = _store.Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>
    /// Relates one conversation to one task. A relation is an insert, not a replacement: a conversation
    /// holds as many as it has tasks it worked on (ADR-0014). An existing pair is returned unchanged
    /// rather than deleted and re-inserted, so its provenance and creation time survive a repeated call.
    /// <paramref name="makePrimaryIfNone"/> promotes the new relation only when the conversation has no
    /// primary yet; the partial unique index keeps that race-free. The callers that use it are the two
    /// places a conversation arrives already assigned to a task — created for it, or restored from a
    /// legacy header that carried it — because an automatic association never assigns one.
    /// </summary>
    public GraphEdge AddSessionTask(string sessionId, string taskId,
        GraphProvenance provenance = GraphProvenance.Manual, bool makePrimaryIfNone = false)
    {
        ValidateSessionTaskPair(sessionId, taskId);
        if (FindSessionTaskEdge(sessionId, taskId) is { } existing) return existing;

        var now = DateTimeOffset.UtcNow;
        var edge = new GraphEdge(Guid.NewGuid().ToString("N"), GraphEntityKind.Task, taskId,
            GraphEntityKind.Session, sessionId, GraphRelationKind.TaskSession, provenance, false, now, now);
        try
        {
            ExecuteNonQuery("INSERT INTO graph_edges (edge_id,from_kind,from_id,to_kind,to_id,relation_kind,provenance,is_primary,created_utc,updated_utc) VALUES ($edge,'task',$task,'session',$session,'task_session',$prov,0,$created,$updated)",
                ("$edge", edge.EdgeId), ("$task", taskId), ("$session", sessionId),
                ("$prov", Provenance(provenance)), ("$created", now.ToString("O")), ("$updated", now.ToString("O")));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 19)
        {
            // Another writer inserted the same pair between the read and the write. Its edge is the one
            // that exists, so report that one instead of failing a call the user already approved.
            if (FindSessionTaskEdge(sessionId, taskId) is { } raced) return raced;
            throw;
        }

        if (makePrimaryIfNone) PromoteSessionTaskIfNone(sessionId, edge.EdgeId);
        return FindSessionTaskEdge(sessionId, taskId) ?? edge;
    }

    /// <summary>Removes one relation. A conversation that loses its primary keeps its other relations
    /// and no primary: the primary is established when a conversation gains its first relation, so
    /// nothing is promoted here (ADR-0014). Returns whether a relation was removed.</summary>
    public bool RemoveSessionTask(string sessionId, string taskId)
    {
        if (FindSessionTaskEdge(sessionId, taskId) is not { } edge) return false;
        RemoveEdge(edge.EdgeId);
        return true;
    }

    /// <summary>
    /// Replaces a conversation's whole relation set in one transaction: the pairs that are no longer
    /// requested are deleted, the missing ones inserted, the kept ones left as they are — so an edge id,
    /// its provenance and its creation time survive a set write that keeps it.
    /// <para>The primary resolves in this order: <paramref name="unassigned"/> makes the conversation
    /// assigned to none of its relations; the requested <paramref name="primaryTaskId"/> when it
    /// is in the set; otherwise the conversation's current primary when it is still in the set;
    /// otherwise the first requested id; and no primary when the set is empty. Every requested id is
    /// validated before the first write, so a refused call writes nothing.</para>
    /// <para>Being assigned to a task is the user's statement about the conversation, so a caller that
    /// has decided the assignment — including deciding there is none — says so with
    /// <paramref name="unassigned"/> rather than relying on the fallback above.</para>
    /// </summary>
    public IReadOnlyList<GraphEdge> SetSessionTasks(string sessionId,
        IReadOnlyCollection<string>? taskIds, string? primaryTaskId = null, bool unassigned = false)
    {
        var session = FindEntity(GraphEntityKind.Session, sessionId)
            ?? throw new EngineeringGraphConstraintException("Session was not registered in the current Workbench.");
        var requested = (taskIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var taskId in requested) ValidateSessionTaskPair(sessionId, taskId);

        var current = SessionTaskEdges(sessionId);
        var keep = new HashSet<string>(requested, StringComparer.Ordinal);
        var primary = ResolvePrimary(requested, primaryTaskId, current, unassigned);
        var now = DateTimeOffset.UtcNow;
        using (var tx = _store.Connection.BeginTransaction())
        {
            foreach (var edge in current.Where(edge => !keep.Contains(edge.FromId)))
                Execute(tx, "DELETE FROM graph_edges WHERE edge_id=$edge", ("$edge", edge.EdgeId));
            foreach (var taskId in requested.Where(taskId => !current.Any(edge => string.Equals(edge.FromId, taskId, StringComparison.Ordinal))))
                Execute(tx, "INSERT INTO graph_edges (edge_id,from_kind,from_id,to_kind,to_id,relation_kind,provenance,is_primary,created_utc,updated_utc) VALUES ($edge,'task',$task,'session',$session,'task_session','manual',0,$created,$updated)",
                    ("$edge", Guid.NewGuid().ToString("N")), ("$task", taskId), ("$session", sessionId),
                    ("$created", now.ToString("O")), ("$updated", now.ToString("O")));
            // Demote before promote: the partial unique index is checked per statement, so promoting
            // first would collide with the primary this write is moving.
            Execute(tx, "UPDATE graph_edges SET is_primary=0 WHERE relation_kind='task_session' AND from_kind='task' AND to_kind='session' AND to_id=$session AND is_primary=1;",
                ("$session", sessionId));
            if (primary is not null)
                Execute(tx, "UPDATE graph_edges SET is_primary=1, updated_utc=$utc WHERE relation_kind='task_session' AND from_kind='task' AND to_kind='session' AND to_id=$session AND from_id=$task;",
                    ("$utc", now.ToString("O")), ("$session", sessionId), ("$task", primary));
            tx.Commit();
        }
        return SessionTaskEdges(sessionId);
    }

    /// <summary>
    /// One conversation's relations, primary first and then by creation, so a reader never has to sort
    /// them. This is also the one-query read a page of conversations uses:
    /// <see cref="ListSessionTaskRelations"/> answers the whole page at once.
    /// </summary>
    public IReadOnlyList<SessionTaskRelation> ListSessionTaskRelations(IReadOnlyCollection<string>? sessionIds)
    {
        var ids = (sessionIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0) return Array.Empty<SessionTaskRelation>();

        using var command = _store.Connection.CreateCommand();
        command.CommandText =
            $"SELECT edge_id,from_id,to_id,provenance,is_primary FROM graph_edges WHERE relation_kind='task_session' AND from_kind='task' AND to_kind='session' AND to_id IN ({Placeholders(ids, "$s")}) ORDER BY to_id, is_primary DESC, created_utc, from_id;";
        for (var index = 0; index < ids.Length; index++)
            command.Parameters.AddWithValue("$s" + index, ids[index]);
        var result = new List<SessionTaskRelation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new SessionTaskRelation(reader.GetString(2), reader.GetString(1), reader.GetString(0),
                ParseProvenance(reader.GetString(3)), reader.GetInt32(4) != 0));
        return result;
    }

    /// <summary>The per-link validation the single-relation path enforced, unchanged: the conversation
    /// must be registered, the task must exist, a worktree task must live in that worktree, and a
    /// device-bound task must belong to the conversation's own device.</summary>
    private void ValidateSessionTaskPair(string sessionId, string taskId)
    {
        var session = FindEntity(GraphEntityKind.Session, sessionId)
            ?? throw new EngineeringGraphConstraintException("Session was not registered in the current Workbench.");
        var task = FindTask(taskId)
            ?? throw new EngineeringGraphConstraintException("The selected task was not found in the current Workbench.", "TASK_NOT_FOUND");
        if (task.ScopeKind == GraphTaskScopeKind.Worktree && task.WorktreeId != session.WorktreeId)
            throw new EngineeringGraphConstraintException("The selected task is not compatible with the current project or Workbench context.");
        if (task.DeviceId is not null && session.DeviceId is not null && task.DeviceId != session.DeviceId)
            throw new EngineeringGraphConstraintException("The selected task is not compatible with the selected PLC device.", "TASK_DEVICE_MISMATCH");
    }

    /// <summary>Promotes one existing relation, but only while the conversation has none. A single
    /// statement, so two concurrent promotions cannot both win: the second one's subquery sees the
    /// first one's primary and promotes nothing.</summary>
    private void PromoteSessionTaskIfNone(string sessionId, string edgeId)
    {
        try
        {
            ExecuteNonQuery(
                "UPDATE graph_edges SET is_primary=1, updated_utc=$utc WHERE edge_id=$edge AND NOT EXISTS (SELECT 1 FROM graph_edges WHERE relation_kind='task_session' AND from_kind='task' AND to_kind='session' AND to_id=$session AND is_primary=1);",
                ("$utc", DateTimeOffset.UtcNow.ToString("O")), ("$edge", edgeId), ("$session", sessionId));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 19)
        {
            // Another writer promoted a relation of the same conversation in between; that primary is
            // the state the caller asked for, so there is nothing left to do.
        }
    }

    /// <summary>Promotes one relation unconditionally, demoting the conversation's current primary
    /// first. The generic relationship route keeps its "this is the primary now" meaning through this
    /// (ADR-0014); the automatic association uses <see cref="PromoteSessionTaskIfNone"/> instead.</summary>
    private GraphEdge PromoteSessionTask(string sessionId, string taskId)
    {
        var now = DateTimeOffset.UtcNow;
        using (var tx = _store.Connection.BeginTransaction())
        {
            Execute(tx, "UPDATE graph_edges SET is_primary=0 WHERE relation_kind='task_session' AND from_kind='task' AND to_kind='session' AND to_id=$session AND is_primary=1;",
                ("$session", sessionId));
            Execute(tx, "UPDATE graph_edges SET is_primary=1, updated_utc=$utc WHERE relation_kind='task_session' AND from_kind='task' AND to_kind='session' AND to_id=$session AND from_id=$task;",
                ("$utc", now.ToString("O")), ("$session", sessionId), ("$task", taskId));
            tx.Commit();
        }
        return FindSessionTaskEdge(sessionId, taskId)
            ?? throw new EngineeringGraphConstraintException("The selected task was not found in the current Workbench.");
    }

    /// <summary>The conversation's relations, ordered primary first then by creation, which is the order
    /// both the projection and the set resolution read them in.</summary>
    private IReadOnlyList<GraphEdge> SessionTaskEdges(string sessionId) =>
        GetIncomingEdges(GraphEntityKind.Session, sessionId)
            .Where(edge => edge.RelationKind == GraphRelationKind.TaskSession && edge.FromKind == GraphEntityKind.Task)
            .OrderByDescending(edge => edge.IsPrimary)
            .ThenBy(edge => edge.CreatedUtc)
            .ThenBy(edge => edge.FromId, StringComparer.Ordinal)
            .ToArray();

    private GraphEdge? FindSessionTaskEdge(string sessionId, string taskId) =>
        SessionTaskEdges(sessionId).FirstOrDefault(edge => string.Equals(edge.FromId, taskId, StringComparison.Ordinal));

    private static string? ResolvePrimary(IReadOnlyCollection<string> requested, string? primaryTaskId, IReadOnlyList<GraphEdge> current, bool unassigned = false)
    {
        // A set write that decides the assignment leaves the conversation related to its tasks and
        // assigned to none of them when it names no task — the state a conversation that created tasks
        // without ever being assigned one is in, and a state the picker has to be able to return to.
        if (requested.Count == 0 || unassigned) return null;
        if (!string.IsNullOrWhiteSpace(primaryTaskId) && requested.Contains(primaryTaskId, StringComparer.Ordinal))
            return primaryTaskId;
        var existing = current.FirstOrDefault(edge => edge.IsPrimary)?.FromId;
        if (existing is not null && requested.Contains(existing, StringComparer.Ordinal)) return existing;
        return requested.First();
    }

    private static string Provenance(GraphProvenance provenance) => provenance.ToString().ToLowerInvariant();

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
        c.CommandText = "SELECT workbench_id,scope_kind,worktree_id,device_id,title,type,status,description,metadata_json,created_utc,updated_utc,target_kind FROM tasks WHERE task_id=$id AND workbench_id=$wb;";
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
            metadata?.Priority ?? 0, metadata?.Intent ?? "", metadata?.ExpectedResult ?? "", r.IsDBNull(3) ? null : r.GetString(3),
            ParseTargetKind(r.IsDBNull(11) ? null : r.GetString(11)));
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
    private int Execute(SqliteTransaction tx,string sql, params (string Name, object? Value)[] ps) { using var c=_store.Connection.CreateCommand(); c.Transaction=tx;c.CommandText=sql;foreach(var p in ps)c.Parameters.AddWithValue(p.Name,p.Value??DBNull.Value);return c.ExecuteNonQuery(); }
    private int ExecuteAll(SqliteTransaction tx, string sql, IEnumerable<(string Name, object? Value)> ps) { using var c = _store.Connection.CreateCommand(); c.Transaction = tx; c.CommandText = sql; foreach (var p in ps) c.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value); return c.ExecuteNonQuery(); }
    private static string Kind(GraphEntityKind k)=>k switch { GraphEntityKind.GitCommit=>"git_commit",GraphEntityKind.SourceObject=>"source_object",GraphEntityKind.SvnRevision=>"svn_revision",GraphEntityKind.Session=>"session",GraphEntityKind.Device=>"device",GraphEntityKind.Worktree=>"worktree", _=>"task" };
    private static GraphEntityKind ParseKind(string value)=>value switch { "git_commit"=>GraphEntityKind.GitCommit,"source_object"=>GraphEntityKind.SourceObject,"svn_revision"=>GraphEntityKind.SvnRevision,"session"=>GraphEntityKind.Session,"device"=>GraphEntityKind.Device,"worktree"=>GraphEntityKind.Worktree,_=>GraphEntityKind.Task };
    private static string Relation(GraphRelationKind k)=>k switch { GraphRelationKind.TaskSession=>"task_session",GraphRelationKind.TaskCommit=>"task_commit",GraphRelationKind.TaskSourceObject=>"task_source_object",GraphRelationKind.TaskSvnRevision=>"task_svn_revision",GraphRelationKind.CommitSourceObject=>"commit_source_object", _=>"commit_svn_revision" };
    private static GraphRelationKind ParseRelation(string value)=>value switch { "task_session"=>GraphRelationKind.TaskSession,"task_commit"=>GraphRelationKind.TaskCommit,"task_source_object"=>GraphRelationKind.TaskSourceObject,"task_svn_revision"=>GraphRelationKind.TaskSvnRevision,"commit_source_object"=>GraphRelationKind.CommitSourceObject,_=>GraphRelationKind.CommitSvnRevision };
    private static GraphProvenance ParseProvenance(string value)=>value switch { "default"=>GraphProvenance.Default,"auto"=>GraphProvenance.Auto,"evidence"=>GraphProvenance.Evidence,_=>GraphProvenance.Manual };
    private static string TargetKind(GraphTaskTargetKind k)=>k switch { GraphTaskTargetKind.Hardware=>"hardware", _=>"device" };
    private static GraphTaskTargetKind ParseTargetKind(string? value)=>value switch { "hardware"=>GraphTaskTargetKind.Hardware, _=>GraphTaskTargetKind.Device };
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
