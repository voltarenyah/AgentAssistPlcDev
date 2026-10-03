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
        Assert.Equal(1, Scalar(store.Connection, "SELECT version FROM graph_schema;"));
        Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'graph_edges';"));
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
            Assert.Equal(1, Scalar(store.Connection, "SELECT version FROM graph_schema;"));
            Assert.Equal(0, Scalar(store.Connection, "SELECT COUNT(*) FROM graph_edges;"));
            Assert.Equal(1, Scalar(store.Connection, "SELECT COUNT(*) FROM prior_data WHERE value = 'Keep me';"));
        }

        SqliteConnection.ClearAllPools();
        var backupBeforeReopen = File.ReadAllBytes(databasePath + ".bak");
        using (var reopened = new EngineeringGraphStore(_root))
            Assert.Equal(1, Scalar(reopened.Connection, "SELECT version FROM graph_schema;"));
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
