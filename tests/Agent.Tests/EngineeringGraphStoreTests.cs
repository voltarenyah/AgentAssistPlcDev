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
