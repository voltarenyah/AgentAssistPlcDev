using Microsoft.Data.Sqlite;
using System.Text.Json;

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
        string intent = "", string expectedResult = "")
    {
        taskId = Require(taskId, nameof(taskId)); title = Require(title, nameof(title));
        if (scope == GraphTaskScopeKind.Worktree && string.IsNullOrWhiteSpace(worktreeId))
            throw new EngineeringGraphConstraintException("A worktree-scoped task requires a worktree ID.");
        if (worktreeId is not null && !_worktreeExists(worktreeId))
            throw new EngineeringGraphConstraintException($"Worktree '{worktreeId}' is not registered in the current Workbench.");
        if (scope == GraphTaskScopeKind.Project && worktreeId is not null)
            throw new EngineeringGraphConstraintException("A project-scoped task cannot have a worktree ID.");
        var now = DateTimeOffset.UtcNow;
        intent = Require(intent, nameof(intent));
        expectedResult = Require(expectedResult, nameof(expectedResult));
        var task = new GraphTask(taskId, _workbenchId, scope, worktreeId, title, type, status, description,
            JsonSerializer.Serialize(new { priority, intent, expectedResult }), now, now, priority, intent, expectedResult);
        using var tx = _store.Connection.BeginTransaction();
        Execute(tx, """
            INSERT INTO tasks (task_id, workbench_id, scope_kind, worktree_id, type, status, title, description, metadata_json, created_utc, updated_utc)
            VALUES ($id,$wb,$scope,$wt,$type,$status,$title,$description,$metadata,$created,$updated);
            INSERT INTO graph_entities (entity_kind, entity_id, workbench_id, worktree_id) VALUES ('task',$id,$wb,$wt);
            """, ("$id", task.TaskId), ("$wb", _workbenchId), ("$scope", task.ScopeKind.ToString().ToLowerInvariant()),
            ("$wt", task.WorktreeId), ("$type", task.Type.ToString().ToLowerInvariant()), ("$status", task.Status.ToString().ToLowerInvariant()),
            ("$title", task.Title), ("$description", task.Description), ("$metadata", task.MetadataJson),
            ("$created", now.ToString("O")), ("$updated", now.ToString("O")));
        tx.Commit();
        return task;
    }

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
            updated.ScopeKind != current.ScopeKind || updated.WorktreeId != current.WorktreeId)
            throw new EngineeringGraphConstraintException("Task identity and scope cannot be changed.");
        var metadata = updated.MetadataJson ?? JsonSerializer.Serialize(new { priority = updated.Priority, intent = updated.Intent, expectedResult = updated.ExpectedResult });
        ExecuteNonQuery("UPDATE tasks SET title=$title,description=$description,status=$status,metadata_json=$metadata,updated_utc=$updated WHERE task_id=$id AND workbench_id=$wb",
            ("$title", updated.Title), ("$description", updated.Description), ("$status", updated.Status.ToString().ToLowerInvariant()),
            ("$metadata", metadata), ("$updated", updated.UpdatedUtc!.Value.ToString("O")), ("$id", taskId), ("$wb", _workbenchId));
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
        command.CommandText = "DELETE FROM tasks WHERE task_id=$id AND workbench_id=$wb; DELETE FROM graph_entities WHERE entity_kind='task' AND entity_id=$id;";
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
        var from = FindEntity(fromKind, fromId) ?? throw new EngineeringGraphConstraintException("Source entity was not registered.");
        var to = FindEntity(toKind, toId) ?? throw new EngineeringGraphConstraintException("Target entity was not registered.");
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
            throw new EngineeringGraphConstraintException("The relationship violates a graph uniqueness constraint.");
        }
        return edge;
    }

    public int CountEdges() => Convert.ToInt32(Scalar("SELECT COUNT(*) FROM graph_edges;"));
    public GraphTask? GetTask(string taskId)
    {
        using var c = _store.Connection.CreateCommand();
        c.CommandText = "SELECT workbench_id,scope_kind,worktree_id,title,type,status,description,metadata_json,created_utc,updated_utc FROM tasks WHERE task_id=$id;";
        c.Parameters.AddWithValue("$id", taskId);
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        var metadata = r.IsDBNull(7) ? null : JsonSerializer.Deserialize<TaskMetadata>(r.GetString(7),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return new GraphTask(taskId, r.GetString(0), Enum.Parse<GraphTaskScopeKind>(r.GetString(1), true),
            r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), Enum.Parse<GraphTaskType>(r.GetString(4), true),
            Enum.Parse<GraphTaskStatus>(r.GetString(5), true), r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7), DateTimeOffset.Parse(r.GetString(8)), DateTimeOffset.Parse(r.GetString(9)),
            metadata?.Priority ?? 0, metadata?.Intent ?? "", metadata?.ExpectedResult ?? "");
    }
    public IReadOnlyList<GraphEdge> GetEdges(GraphEntityKind fromKind, string fromId, GraphEntityKind? toKind = null) =>
        ReadEdges(fromKind, fromId, toKind);

    private IReadOnlyList<GraphEdge> ReadEdges(GraphEntityKind fromKind, string fromId, GraphEntityKind? toKind)
    {
        using var c = _store.Connection.CreateCommand();
        c.CommandText = "SELECT edge_id,to_kind,to_id,relation_kind,provenance,is_primary,created_utc,updated_utc FROM graph_edges WHERE from_kind=$fk AND from_id=$id" +
            (toKind is null ? "" : " AND to_kind=$tk") + " ORDER BY created_utc;";
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
    private void ExecuteNonQuery(string sql, params (string Name, object? Value)[] ps) { using var c=_store.Connection.CreateCommand(); c.CommandText=sql; foreach(var p in ps)c.Parameters.AddWithValue(p.Name,p.Value??DBNull.Value); c.ExecuteNonQuery(); }
    private void Execute(SqliteTransaction tx,string sql, params (string Name, object? Value)[] ps) { using var c=_store.Connection.CreateCommand(); c.Transaction=tx;c.CommandText=sql;foreach(var p in ps)c.Parameters.AddWithValue(p.Name,p.Value??DBNull.Value);c.ExecuteNonQuery(); }
    private static string Kind(GraphEntityKind k)=>k switch { GraphEntityKind.GitCommit=>"git_commit",GraphEntityKind.SourceObject=>"source_object",GraphEntityKind.SvnRevision=>"svn_revision",GraphEntityKind.Session=>"session", _=>"task" };
    private static GraphEntityKind ParseKind(string value)=>value switch { "git_commit"=>GraphEntityKind.GitCommit,"source_object"=>GraphEntityKind.SourceObject,"svn_revision"=>GraphEntityKind.SvnRevision,"session"=>GraphEntityKind.Session,_=>GraphEntityKind.Task };
    private static string Relation(GraphRelationKind k)=>k switch { GraphRelationKind.TaskSession=>"task_session",GraphRelationKind.TaskCommit=>"task_commit",GraphRelationKind.TaskSourceObject=>"task_source_object",GraphRelationKind.TaskSvnRevision=>"task_svn_revision",GraphRelationKind.CommitSourceObject=>"commit_source_object", _=>"commit_svn_revision" };
    private static GraphRelationKind ParseRelation(string value)=>value switch { "task_session"=>GraphRelationKind.TaskSession,"task_commit"=>GraphRelationKind.TaskCommit,"task_source_object"=>GraphRelationKind.TaskSourceObject,"task_svn_revision"=>GraphRelationKind.TaskSvnRevision,"commit_source_object"=>GraphRelationKind.CommitSourceObject,_=>GraphRelationKind.CommitSvnRevision };
    private static GraphProvenance ParseProvenance(string value)=>value switch { "default"=>GraphProvenance.Default,"evidence"=>GraphProvenance.Evidence,_=>GraphProvenance.Manual };
    private sealed record TaskMetadata(int Priority, string Intent, string ExpectedResult);
    private static string Require(string? value,string name)=>string.IsNullOrWhiteSpace(value)?throw new ArgumentException("A value is required.",name):value;
}
