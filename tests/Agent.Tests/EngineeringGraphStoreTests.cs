using Agent.Workbench.EngineeringGraph;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Agent.Tests;

public sealed class EngineeringGraphStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"engineering-graph-{Guid.NewGuid():N}");

    [Fact]
    public void CreatesDatabaseUnderTrustedAutomationDirectoryAndCreatesSchema()
    {
        using var store = new EngineeringGraphStore(_root);

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, ".automation", "engineering.db")), store.DatabasePath);
        Assert.StartsWith(Path.GetFullPath(_root), store.DatabasePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(store.DatabasePath));
        Assert.Equal(EngineeringGraphSchema.CurrentVersion,
            Scalar(store.Connection, "SELECT COALESCE(MAX(version), 0) FROM graph_schema;"));
        Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'graph_edges';"));
        Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'graph_entity_properties';"));
    }

    [Fact]
    public void FailedMigrationLeavesPriorSchemaAndDataReadable()
    {
        var databaseDirectory = Path.Combine(_root, ".automation");
        Directory.CreateDirectory(databaseDirectory);
        var databasePath = Path.Combine(databaseDirectory, "engineering.db");
        using (var initial = new SqliteConnection($"Data Source={databasePath}"))
        {
            initial.Open();
            using var command = initial.CreateCommand();
            command.CommandText = """
                CREATE TABLE graph_schema (version INTEGER NOT NULL, applied_utc TEXT NOT NULL);
                INSERT INTO graph_schema VALUES (0, 'before');
                CREATE TABLE prior_data (value TEXT NOT NULL);
                INSERT INTO prior_data VALUES ('Keep me');
                """;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(databasePath);
        Assert.Throws<InvalidOperationException>(() =>
            new EngineeringGraphStore(_root, phase =>
            {
                if (phase == 2)
                    throw new InvalidOperationException("Injected migration failure.");
            }));
        Assert.True(File.Exists(databasePath + ".bak"));

        using (var readable = new SqliteConnection($"Data Source={databasePath}"))
        {
            readable.Open();
            Assert.Equal(0, Scalar(readable, "SELECT version FROM graph_schema;"));
            Assert.Equal(1, Scalar(readable, "SELECT COUNT(*) FROM prior_data WHERE value = 'Keep me';"));
        }
        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(databasePath));
    }

    [Fact]
    public void SuccessfulMigrationRetainsPriorDataAndBacksUpPreMigrationDatabase()
    {
        var databaseDirectory = Path.Combine(_root, ".automation");
        Directory.CreateDirectory(databaseDirectory);
        var databasePath = Path.Combine(databaseDirectory, "engineering.db");
        using (var initial = new SqliteConnection($"Data Source={databasePath}"))
        {
            initial.Open();
            using var command = initial.CreateCommand();
            command.CommandText = """
                CREATE TABLE graph_schema (version INTEGER NOT NULL, applied_utc TEXT NOT NULL);
                INSERT INTO graph_schema VALUES (0, 'before');
                CREATE TABLE prior_data (value TEXT NOT NULL);
                INSERT INTO prior_data VALUES ('Keep me');
                """;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        using (var store = new EngineeringGraphStore(_root))
        {
            Assert.Equal(EngineeringGraphSchema.CurrentVersion,
                Scalar(store.Connection, "SELECT COALESCE(MAX(version), 0) FROM graph_schema;"));
            Assert.Equal(0, Scalar(store.Connection, "SELECT COUNT(*) FROM graph_edges;"));
            Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM prior_data WHERE value = 'Keep me';"));
        }

        SqliteConnection.ClearAllPools();
        var backupBeforeReopen = File.ReadAllBytes(databasePath + ".bak");
        using (var reopened = new EngineeringGraphStore(_root))
            Assert.Equal(EngineeringGraphSchema.CurrentVersion,
                Scalar(reopened.Connection, "SELECT COALESCE(MAX(version), 0) FROM graph_schema;"));
        SqliteConnection.ClearAllPools();
        Assert.Equal(backupBeforeReopen, File.ReadAllBytes(databasePath + ".bak"));

        using var backup = new SqliteConnection($"Data Source={databasePath}.bak");
        backup.Open();
        Assert.Equal(0, Scalar(backup, "SELECT version FROM graph_schema;"));
        Assert.Equal(1, Scalar(backup, "SELECT COUNT(*) FROM prior_data WHERE value = 'Keep me';"));
        Assert.Equal(0, Scalar(backup, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'graph_edges';"));
    }

    [Fact]
    public void AppliesABusyTimeoutSoABlockedStatementWaitsInsteadOfFailing()
    {
        using var store = new EngineeringGraphStore(_root);

        Assert.Equal("5000", Text(store.Connection, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public void OpeningASecondStoreTakesNoWriterLockWhileTheSchemaIsCurrent()
    {
        using var first = new EngineeringGraphStore(_root);
        // Stand in for an in-flight graph write, the way another request's scope holds it.
        using var inFlight = first.Connection.BeginTransaction();
        using (var write = first.Connection.CreateCommand())
        {
            write.Transaction = inFlight;
            write.CommandText = """
                INSERT OR REPLACE INTO graph_entities (entity_kind,entity_id,workbench_id,worktree_id,device_id,external_ref)
                VALUES ('source_object','dev-1:block-main','wb-1','wt-1','dev-1','Blocks/Main.xml');
                """;
            Assert.Equal(1, write.ExecuteNonQuery());
        }

        // The task page opens several graph scopes per interaction. Reading the schema version must not
        // need the writer lock: while the constructor opened its migration transaction unconditionally
        // this second scope waited out the driver's retry window and failed with "database is locked".
        using var second = new EngineeringGraphStore(_root);

        Assert.Equal(EngineeringGraphSchema.CurrentVersion,
            Scalar(second.Connection, "SELECT COALESCE(MAX(version), 0) FROM graph_schema;"));
    }

    [Fact]
    public void MigratesAVersionFiveDatabaseToVersionSixWithoutLosingRows()
    {
        var databasePath = CreateVersionFiveDatabase();
        SqliteConnection.ClearAllPools();

        using (var store = new EngineeringGraphStore(_root))
        {
            Assert.Equal(EngineeringGraphSchema.CurrentVersion,
                Scalar(store.Connection, "SELECT COALESCE(MAX(version), 0) FROM graph_schema;"));
            Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'graph_entity_properties';"));
            // The property table starts empty and fills on the first projection of each device.
            Assert.Equal(0, Scalar(store.Connection, "SELECT COUNT(*) FROM graph_entity_properties;"));

            // Task history, entities, edges, staged source objects and file evidence all survive.
            Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM tasks;"));
            Assert.Equal("Fix the block", Text(store.Connection, "SELECT title FROM tasks WHERE task_id = 'task-1';"));
            Assert.Equal(3, Scalar(store.Connection, "SELECT COUNT(*) FROM graph_entities;"));
            Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM graph_edges;"));
            Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM task_source_stages;"));
            Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM graph_file_evidence;"));
            Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM legacy_imports;"));
            Assert.Equal("Blocks/Main [OB1].xml", Text(store.Connection,
                "SELECT external_ref FROM graph_entities WHERE entity_id = 'dev-1:block-main';"));

            // No cascading delete from graph_entities: node registration cannot take a device's
            // properties with it (ADR-0011, Negative Consequences).
            Assert.Equal(0, Scalar(store.Connection, "SELECT COUNT(*) FROM pragma_foreign_key_list('graph_entity_properties');"));
        }

        // The pre-migration backup holds the version-five database, not the migrated one.
        Assert.True(File.Exists(databasePath + ".bak"));
        SqliteConnection.ClearAllPools();
        using var backup = new SqliteConnection($"Data Source={databasePath}.bak");
        backup.Open();
        Assert.Equal(5, Scalar(backup, "SELECT COALESCE(MAX(version), 0) FROM graph_schema;"));
        Assert.Equal(0, Scalar(backup, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'graph_entity_properties';"));
        Assert.Equal(1, Scalar(backup, "SELECT COUNT(*) FROM tasks;"));
        Assert.Equal(1, Scalar(backup, "SELECT COUNT(*) FROM graph_edges;"));
    }

    [Fact]
    public void FailedVersionSixMigrationLeavesTheDatabaseByteIdentical()
    {
        var databasePath = CreateVersionFiveDatabase();
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(databasePath);

        // Phase seven is the version-six step's second injection point: the property table exists
        // inside the transaction and the version row is not yet written.
        Assert.Throws<InvalidOperationException>(() =>
            new EngineeringGraphStore(_root, phase =>
            {
                if (phase == 7)
                    throw new InvalidOperationException("Injected migration failure.");
            }));
        Assert.True(File.Exists(databasePath + ".bak"));

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(databasePath));
        using var readable = new SqliteConnection($"Data Source={databasePath}");
        readable.Open();
        Assert.Equal(5, Scalar(readable, "SELECT COALESCE(MAX(version), 0) FROM graph_schema;"));
        Assert.Equal(0, Scalar(readable, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'graph_entity_properties';"));
        Assert.Equal(1, Scalar(readable, "SELECT COUNT(*) FROM graph_edges;"));
    }

    [Fact]
    public void MigratesAVersionSixDatabaseToVersionSevenAndPromotesOnlyTheSingleRelation()
    {
        var databasePath = CreateVersionSixDatabase();
        SqliteConnection.ClearAllPools();

        using (var store = new EngineeringGraphStore(_root))
        {
            Assert.Equal(7, Scalar(store.Connection, "SELECT COALESCE(MAX(version), 0) FROM graph_schema;"));
            Assert.Equal(1, Scalar(store.Connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_graph_edges_primary_task_session';"));

            // Every row survives the step.
            Assert.Equal(2, Scalar(store.Connection, "SELECT COUNT(*) FROM tasks;"));
            Assert.Equal(7, Scalar(store.Connection, "SELECT COUNT(*) FROM graph_entities;"));
            Assert.Equal(5, Scalar(store.Connection, "SELECT COUNT(*) FROM graph_edges;"));
            Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM task_source_stages;"));
            Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM graph_file_evidence;"));
            Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM legacy_imports;"));

            // The conversation with one relation is promoted, the conversation that already carried a
            // primary keeps exactly one and its second relation is left alone, and the primary commit
            // relation is untouched.
            Assert.Equal(1, Scalar(store.Connection, "SELECT is_primary FROM graph_edges WHERE edge_id = 'edge-single';"));
            Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM graph_edges WHERE to_id = 'session-two' AND is_primary = 1;"));
            Assert.Equal(0, Scalar(store.Connection, "SELECT is_primary FROM graph_edges WHERE edge_id = 'edge-two-b';"));
            Assert.Equal(1, Scalar(store.Connection, "SELECT is_primary FROM graph_edges WHERE edge_id = 'edge-commit';"));
        }

        // A migrated database is current, so re-opening it writes nothing.
        SqliteConnection.ClearAllPools();
        using (var reopened = new EngineeringGraphStore(_root))
            Assert.Equal(7, Scalar(reopened.Connection, "SELECT COALESCE(MAX(version), 0) FROM graph_schema;"));
    }

    [Fact]
    public void FailedVersionSevenMigrationLeavesTheDatabaseByteIdentical()
    {
        var databasePath = CreateVersionSixDatabase();
        SqliteConnection.ClearAllPools();
        var before = File.ReadAllBytes(databasePath);

        // Phase nine is the version-seven step's second injection point: the promotion and the index
        // exist inside the transaction and the version row is not yet written.
        Assert.Throws<InvalidOperationException>(() =>
            new EngineeringGraphStore(_root, phase =>
            {
                if (phase == 9)
                    throw new InvalidOperationException("Injected migration failure.");
            }));
        Assert.True(File.Exists(databasePath + ".bak"));

        SqliteConnection.ClearAllPools();
        Assert.Equal(before, File.ReadAllBytes(databasePath));
        using var readable = new SqliteConnection($"Data Source={databasePath}");
        readable.Open();
        Assert.Equal(6, Scalar(readable, "SELECT COALESCE(MAX(version), 0) FROM graph_schema;"));
        Assert.Equal(0, Scalar(readable,
            "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ux_graph_edges_primary_task_session';"));
        Assert.Equal(0, Scalar(readable, "SELECT is_primary FROM graph_edges WHERE edge_id = 'edge-single';"));
    }

    /// <summary>Builds the version-five database exactly as it shipped, so the migration is exercised
    /// against the real prior state rather than a fresh schema. The DDL is frozen history.</summary>
    private string CreateVersionFiveDatabase()
    {
        var databaseDirectory = Path.Combine(_root, ".automation");
        Directory.CreateDirectory(databaseDirectory);
        var databasePath = Path.Combine(databaseDirectory, "engineering.db");
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE graph_schema (version INTEGER NOT NULL, applied_utc TEXT NOT NULL);
            INSERT INTO graph_schema VALUES (1,'v1'),(2,'v2'),(3,'v3'),(4,'v4'),(5,'v5');
            CREATE TABLE tasks (task_id TEXT NOT NULL PRIMARY KEY, workbench_id TEXT NOT NULL, scope_kind TEXT NOT NULL,
                worktree_id TEXT NULL, type TEXT NOT NULL, status TEXT NOT NULL, title TEXT NOT NULL, description TEXT NULL,
                metadata_json TEXT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                device_id TEXT NULL, target_kind TEXT NULL);
            CREATE TABLE graph_entities (entity_kind TEXT NOT NULL, entity_id TEXT NOT NULL, workbench_id TEXT NOT NULL,
                worktree_id TEXT NULL, device_id TEXT NULL, external_ref TEXT NULL, PRIMARY KEY (entity_kind, entity_id));
            CREATE TABLE graph_edges (edge_id TEXT NOT NULL PRIMARY KEY, from_kind TEXT NOT NULL, from_id TEXT NOT NULL,
                to_kind TEXT NOT NULL, to_id TEXT NOT NULL, relation_kind TEXT NOT NULL, provenance TEXT NOT NULL,
                is_primary INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0, 1)), created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL, UNIQUE (from_kind, from_id, to_kind, to_id, relation_kind));
            CREATE TABLE legacy_imports (source_kind TEXT NOT NULL, source_id TEXT NOT NULL, imported_utc TEXT NOT NULL,
                PRIMARY KEY (source_kind, source_id));
            CREATE TABLE graph_file_evidence (commit_sha TEXT NOT NULL, relative_path TEXT NOT NULL, recorded_utc TEXT NOT NULL,
                PRIMARY KEY (commit_sha, relative_path));
            CREATE TABLE task_source_stages (task_id TEXT NOT NULL, source_object_id TEXT NOT NULL, device_id TEXT NOT NULL,
                baseline_evidence_json TEXT NULL, staged_utc TEXT NOT NULL, released_utc TEXT NULL, worktree_id TEXT NULL,
                PRIMARY KEY (task_id, source_object_id));
            CREATE UNIQUE INDEX ux_task_source_stages_active_source ON task_source_stages (worktree_id, source_object_id)
                WHERE released_utc IS NULL;
            INSERT INTO tasks VALUES ('task-1','wb-1','worktree','wt-1','feature','todo','Fix the block','desc',NULL,
                '2026-01-01T00:00:00.0000000+00:00','2026-01-01T00:00:00.0000000+00:00','dev-1','device');
            INSERT INTO graph_entities VALUES ('task','task-1','wb-1','wt-1','dev-1',NULL);
            INSERT INTO graph_entities VALUES ('source_object','dev-1:block-main','wb-1','wt-1','dev-1','Blocks/Main [OB1].xml');
            INSERT INTO graph_entities VALUES ('git_commit','sha-1','wb-1','wt-1',NULL,NULL);
            INSERT INTO graph_edges VALUES ('edge-1','task','task-1','source_object','dev-1:block-main','task_source_object',
                'manual',0,'2026-01-01T00:00:00.0000000+00:00','2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO task_source_stages VALUES ('task-1','dev-1:block-main','dev-1',NULL,
                '2026-01-01T00:00:00.0000000+00:00',NULL,'wt-1');
            INSERT INTO graph_file_evidence VALUES ('sha-1','Blocks/Main [OB1].xml','2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO legacy_imports VALUES ('tasks.json','legacy-1','2026-01-01T00:00:00.0000000+00:00');
            """;
        command.ExecuteNonQuery();
        return databasePath;
    }

    /// <summary>Builds the version-six database as it shipped, so the version-seven step is exercised
    /// against the state a real workbench upgrades from: a conversation with one relation (promoted), a
    /// conversation that already carries a primary among two relations (left alone), a conversation with
    /// no relation, and a primary commit relation the step must not touch.</summary>
    private string CreateVersionSixDatabase()
    {
        var databaseDirectory = Path.Combine(_root, ".automation");
        Directory.CreateDirectory(databaseDirectory);
        var databasePath = Path.Combine(databaseDirectory, "engineering.db");
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE graph_schema (version INTEGER NOT NULL, applied_utc TEXT NOT NULL);
            INSERT INTO graph_schema VALUES (1,'v1'),(2,'v2'),(3,'v3'),(4,'v4'),(5,'v5'),(6,'v6');
            CREATE TABLE tasks (task_id TEXT NOT NULL PRIMARY KEY, workbench_id TEXT NOT NULL, scope_kind TEXT NOT NULL,
                worktree_id TEXT NULL, type TEXT NOT NULL, status TEXT NOT NULL, title TEXT NOT NULL, description TEXT NULL,
                metadata_json TEXT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                device_id TEXT NULL, target_kind TEXT NULL);
            CREATE TABLE graph_entities (entity_kind TEXT NOT NULL, entity_id TEXT NOT NULL, workbench_id TEXT NOT NULL,
                worktree_id TEXT NULL, device_id TEXT NULL, external_ref TEXT NULL, PRIMARY KEY (entity_kind, entity_id));
            CREATE TABLE graph_edges (edge_id TEXT NOT NULL PRIMARY KEY, from_kind TEXT NOT NULL, from_id TEXT NOT NULL,
                to_kind TEXT NOT NULL, to_id TEXT NOT NULL, relation_kind TEXT NOT NULL, provenance TEXT NOT NULL,
                is_primary INTEGER NOT NULL DEFAULT 0 CHECK (is_primary IN (0, 1)), created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL, UNIQUE (from_kind, from_id, to_kind, to_id, relation_kind));
            CREATE TABLE legacy_imports (source_kind TEXT NOT NULL, source_id TEXT NOT NULL, imported_utc TEXT NOT NULL,
                PRIMARY KEY (source_kind, source_id));
            CREATE TABLE graph_file_evidence (commit_sha TEXT NOT NULL, relative_path TEXT NOT NULL, recorded_utc TEXT NOT NULL,
                PRIMARY KEY (commit_sha, relative_path));
            CREATE TABLE task_source_stages (task_id TEXT NOT NULL, source_object_id TEXT NOT NULL, device_id TEXT NOT NULL,
                baseline_evidence_json TEXT NULL, staged_utc TEXT NOT NULL, released_utc TEXT NULL, worktree_id TEXT NULL,
                PRIMARY KEY (task_id, source_object_id));
            CREATE UNIQUE INDEX ux_task_source_stages_active_source ON task_source_stages (worktree_id, source_object_id)
                WHERE released_utc IS NULL;
            CREATE TABLE graph_entity_properties (entity_kind TEXT NOT NULL, entity_id TEXT NOT NULL, name TEXT NOT NULL,
                value_kind TEXT NOT NULL, value_text TEXT NULL, value_number REAL NULL, value_flag INTEGER NULL,
                value_timestamp TEXT NULL, value_json TEXT NULL, source TEXT NOT NULL,
                PRIMARY KEY (entity_kind, entity_id, name));
            CREATE INDEX ix_graph_entity_properties_entity ON graph_entity_properties (entity_kind, entity_id);
            CREATE UNIQUE INDEX ux_graph_edges_primary_git_commit ON graph_edges (to_kind, to_id)
                WHERE relation_kind = 'task_commit' AND is_primary = 1 AND from_kind = 'task' AND to_kind = 'git_commit';
            INSERT INTO tasks VALUES ('task-1','wb-1','worktree','wt-1','feature','todo','Fix the block','desc',NULL,
                '2026-01-01T00:00:00.0000000+00:00','2026-01-01T00:00:00.0000000+00:00','dev-1','device');
            INSERT INTO tasks VALUES ('task-2','wb-1','worktree','wt-1','issue','todo','Second task',NULL,NULL,
                '2026-01-01T00:00:00.0000000+00:00','2026-01-01T00:00:00.0000000+00:00','dev-1','device');
            INSERT INTO graph_entities VALUES ('task','task-1','wb-1','wt-1','dev-1',NULL);
            INSERT INTO graph_entities VALUES ('task','task-2','wb-1','wt-1','dev-1',NULL);
            INSERT INTO graph_entities VALUES ('session','session-one','wb-1','wt-1','dev-1',NULL);
            INSERT INTO graph_entities VALUES ('session','session-two','wb-1','wt-1','dev-1',NULL);
            INSERT INTO graph_entities VALUES ('session','session-none','wb-1','wt-1','dev-1',NULL);
            INSERT INTO graph_entities VALUES ('source_object','dev-1:block-main','wb-1','wt-1','dev-1','Blocks/Main [OB1].xml');
            INSERT INTO graph_entities VALUES ('git_commit','sha-1','wb-1','wt-1',NULL,NULL);
            INSERT INTO graph_edges VALUES ('edge-single','task','task-1','session','session-one','task_session',
                'default',0,'2026-01-01T00:00:00.0000000+00:00','2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO graph_edges VALUES ('edge-two-a','task','task-1','session','session-two','task_session',
                'default',1,'2026-01-01T00:00:00.0000000+00:00','2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO graph_edges VALUES ('edge-two-b','task','task-2','session','session-two','task_session',
                'manual',0,'2026-01-01T00:00:00.0000000+00:00','2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO graph_edges VALUES ('edge-commit','task','task-1','git_commit','sha-1','task_commit',
                'evidence',1,'2026-01-01T00:00:00.0000000+00:00','2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO graph_edges VALUES ('edge-stage','task','task-1','source_object','dev-1:block-main','task_source_object',
                'manual',0,'2026-01-01T00:00:00.0000000+00:00','2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO task_source_stages VALUES ('task-1','dev-1:block-main','dev-1',NULL,
                '2026-01-01T00:00:00.0000000+00:00',NULL,'wt-1');
            INSERT INTO graph_file_evidence VALUES ('sha-1','Blocks/Main [OB1].xml','2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO legacy_imports VALUES ('tasks.json','legacy-1','2026-01-01T00:00:00.0000000+00:00');
            """;
        command.ExecuteNonQuery();
        return databasePath;
    }

    private static string Text(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
