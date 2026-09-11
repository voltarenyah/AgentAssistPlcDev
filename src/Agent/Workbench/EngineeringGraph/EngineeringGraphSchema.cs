using Microsoft.Data.Sqlite;

namespace Agent.Workbench.EngineeringGraph;

public static class EngineeringGraphSchema
{
    public const int CurrentVersion = 1;

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
