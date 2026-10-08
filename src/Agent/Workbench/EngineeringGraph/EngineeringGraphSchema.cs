using Microsoft.Data.Sqlite;

namespace Agent.Workbench.EngineeringGraph;

public static class EngineeringGraphSchema
{
    public const int CurrentVersion = 7;

    internal static int GetVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'graph_schema';";
        if (command.ExecuteScalar() is null)
            return 0;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM graph_schema;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    internal static void Apply(SqliteConnection connection, SqliteTransaction transaction, Action<int>? failureInjector)
    {
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS graph_schema (
                version INTEGER NOT NULL,
                applied_utc TEXT NOT NULL
            );
            """);

        var version = ReadVersion(connection, transaction);
        if (version > CurrentVersion)
        {
            throw new InvalidOperationException($"Engineering graph schema version '{version}' is newer than supported version '{CurrentVersion}'.");
        }

        if (version < 1)
        {
            failureInjector?.Invoke(1);
            Execute(connection, transaction, """
                CREATE TABLE IF NOT EXISTS tasks (
                    task_id TEXT NOT NULL PRIMARY KEY,
                    workbench_id TEXT NOT NULL,
                    scope_kind TEXT NOT NULL,
                    worktree_id TEXT NULL,
                    type TEXT NOT NULL,
                    status TEXT NOT NULL,
                    title TEXT NOT NULL,
                    description TEXT NULL,
                    metadata_json TEXT NULL,
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS graph_entities (
                    entity_kind TEXT NOT NULL,
                    entity_id TEXT NOT NULL,
                    workbench_id TEXT NOT NULL,
                    worktree_id TEXT NULL,
                    device_id TEXT NULL,
                    external_ref TEXT NULL,
                    PRIMARY KEY (entity_kind, entity_id)
                );
                CREATE TABLE IF NOT EXISTS graph_edges (
                    edge_id TEXT NOT NULL PRIMARY KEY,
                    from_kind TEXT NOT NULL,
                    from_id TEXT NOT NULL,
                    to_kind TEXT NOT NULL,
                    to_id TEXT NOT NULL,
                    relation_kind TEXT NOT NULL,
                    provenance TEXT NOT NULL,
                    is_primary INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0, 1)),
                    created_utc TEXT NOT NULL,
                    updated_utc TEXT NOT NULL,
                    UNIQUE (from_kind, from_id, to_kind, to_id, relation_kind)
                );
                CREATE TABLE IF NOT EXISTS legacy_imports (
                    source_kind TEXT NOT NULL,
                    source_id TEXT NOT NULL,
                    imported_utc TEXT NOT NULL,
                    PRIMARY KEY (source_kind, source_id)
                );
                CREATE TABLE IF NOT EXISTS graph_file_evidence (
                    commit_sha TEXT NOT NULL,
                    relative_path TEXT NOT NULL,
                    recorded_utc TEXT NOT NULL,
                    PRIMARY KEY (commit_sha, relative_path)
                );
                CREATE INDEX IF NOT EXISTS ix_graph_entities_workbench
                    ON graph_entities (workbench_id, entity_kind);
                CREATE INDEX IF NOT EXISTS ix_graph_edges_from
                    ON graph_edges (from_kind, from_id);
                CREATE INDEX IF NOT EXISTS ix_graph_edges_to
                    ON graph_edges (to_kind, to_id);
                CREATE UNIQUE INDEX IF NOT EXISTS ux_graph_edges_primary_git_commit
                    ON graph_edges (to_kind, to_id)
                    WHERE relation_kind = 'task_commit' AND is_primary = 1
                        AND from_kind = 'task' AND to_kind = 'git_commit';
                """);
            failureInjector?.Invoke(2);
            Execute(connection, transaction, "DELETE FROM graph_schema;");
            Execute(connection, transaction, "INSERT INTO graph_schema (version, applied_utc) VALUES (1, $utc);",
                ("$utc", DateTimeOffset.UtcNow.ToString("O")));
        }
        if (version < 2)
        {
            Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS graph_file_evidence (commit_sha TEXT NOT NULL, relative_path TEXT NOT NULL, recorded_utc TEXT NOT NULL, PRIMARY KEY (commit_sha, relative_path));");
            Execute(connection, transaction, "INSERT INTO graph_schema (version, applied_utc) VALUES (2, $utc);",
                ("$utc", DateTimeOffset.UtcNow.ToString("O")));
        }
        if (version < 3)
        {
            Execute(connection, transaction, "ALTER TABLE tasks ADD COLUMN device_id TEXT NULL;");
            Execute(connection, transaction, """
                CREATE TABLE task_source_stages (
                    task_id TEXT NOT NULL,
                    source_object_id TEXT NOT NULL,
                    device_id TEXT NOT NULL,
                    baseline_evidence_json TEXT NULL,
                    staged_utc TEXT NOT NULL,
                    released_utc TEXT NULL,
                    PRIMARY KEY (task_id, source_object_id)
                );
                CREATE UNIQUE INDEX ux_task_source_stages_active_source
                    ON task_source_stages (source_object_id) WHERE released_utc IS NULL;
                """);
            Execute(connection, transaction, "INSERT INTO graph_schema (version, applied_utc) VALUES (3, $utc);",
                ("$utc", DateTimeOffset.UtcNow.ToString("O")));
        }
        if (version < 4)
        {
            Execute(connection, transaction, "DROP INDEX IF EXISTS ux_task_source_stages_active_source;");
            Execute(connection, transaction, "ALTER TABLE task_source_stages ADD COLUMN worktree_id TEXT NULL;");
            Execute(connection, transaction, "UPDATE task_source_stages SET worktree_id=(SELECT worktree_id FROM tasks WHERE tasks.task_id=task_source_stages.task_id);");
            Execute(connection, transaction, "CREATE UNIQUE INDEX ux_task_source_stages_active_source ON task_source_stages (worktree_id, source_object_id) WHERE released_utc IS NULL;");
            Execute(connection, transaction, "INSERT INTO graph_schema (version, applied_utc) VALUES (4, $utc);",
                ("$utc", DateTimeOffset.UtcNow.ToString("O")));
        }
        if (version < 5)
        {
            Execute(connection, transaction, "ALTER TABLE tasks ADD COLUMN target_kind TEXT NULL;");
            Execute(connection, transaction, "INSERT INTO graph_schema (version, applied_utc) VALUES (5, $utc);",
                ("$utc", DateTimeOffset.UtcNow.ToString("O")));
        }
        if (version < 6)
        {
            failureInjector?.Invoke(6);
            // Properties are keyed by the node they belong to and deliberately carry no foreign key to
            // graph_entities. Node registration uses INSERT OR REPLACE, which deletes the replaced row:
            // with ForeignKeys enabled an ON DELETE CASCADE here would silently drop a device's whole
            // fact set on the next stage click (ADR-0011, Negative Consequences).
            Execute(connection, transaction, """
                CREATE TABLE IF NOT EXISTS graph_entity_properties (
                    entity_kind TEXT NOT NULL,
                    entity_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    value_kind TEXT NOT NULL,
                    value_text TEXT NULL,
                    value_number REAL NULL,
                    value_flag INTEGER NULL CHECK (value_flag IN (0, 1)),
                    value_timestamp TEXT NULL,
                    value_json TEXT NULL,
                    source TEXT NOT NULL,
                    PRIMARY KEY (entity_kind, entity_id, name)
                );
                CREATE INDEX IF NOT EXISTS ix_graph_entity_properties_entity
                    ON graph_entity_properties (entity_kind, entity_id);
                """);
            failureInjector?.Invoke(7);
            Execute(connection, transaction, "INSERT INTO graph_schema (version, applied_utc) VALUES (6, $utc);",
                ("$utc", DateTimeOffset.UtcNow.ToString("O")));
        }
        if (version < 7)
        {
            failureInjector?.Invoke(8);
            // A conversation's relation to a task was one edge before this version, so the relation of a
            // conversation that has exactly one and no primary yet becomes its primary. The
            // SUM(is_primary) = 0 half of the group condition is required: filtering the group on
            // is_primary = 0 alone would make a conversation that already has two relations and a primary
            // look like a one-relation conversation, promote its other edge, and fail at index creation.
            Execute(connection, transaction, """
                UPDATE graph_edges SET is_primary = 1, updated_utc = $utc
                 WHERE relation_kind = 'task_session' AND from_kind = 'task' AND to_kind = 'session'
                   AND is_primary = 0
                   AND to_id IN (
                       SELECT to_id FROM graph_edges
                        WHERE relation_kind = 'task_session' AND from_kind = 'task' AND to_kind = 'session'
                        GROUP BY to_id
                       HAVING COUNT(*) = 1 AND SUM(is_primary) = 0);
                """, ("$utc", DateTimeOffset.UtcNow.ToString("O")));
            Execute(connection, transaction, """
                CREATE UNIQUE INDEX IF NOT EXISTS ux_graph_edges_primary_task_session
                    ON graph_edges (to_kind, to_id)
                    WHERE relation_kind = 'task_session' AND is_primary = 1
                      AND from_kind = 'task' AND to_kind = 'session';
                """);
            failureInjector?.Invoke(9);
            Execute(connection, transaction, "INSERT INTO graph_schema (version, applied_utc) VALUES (7, $utc);",
                ("$utc", DateTimeOffset.UtcNow.ToString("O")));
        }
    }

    private static int ReadVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM graph_schema;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
