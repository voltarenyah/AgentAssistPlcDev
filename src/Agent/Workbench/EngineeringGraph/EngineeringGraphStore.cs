using Microsoft.Data.Sqlite;

namespace Agent.Workbench.EngineeringGraph;

public sealed class EngineeringGraphStore : IDisposable
{
    public const string DatabaseFileName = "engineering.db";
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public EngineeringGraphStore(string workbenchRoot, Action<int>? migrationFailureInjector = null)
    {
        WorkbenchRoot = WorkbenchPaths.ResolveWorkbench("workbench", workbenchRoot);
        DatabasePath = WorkbenchPaths.ResolveRelative(
            WorkbenchRoot,
            Path.Combine(".automation", DatabaseFileName));

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        try
        {
            _connection.Open();
            var currentVersion = EngineeringGraphSchema.GetVersion(_connection);
            if (currentVersion < EngineeringGraphSchema.CurrentVersion)
                BackupBeforeMigration(_connection, DatabasePath);
            using var transaction = _connection.BeginTransaction();
            EngineeringGraphSchema.Apply(_connection, transaction, migrationFailureInjector);
            transaction.Commit();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public string WorkbenchRoot { get; }
    public string DatabasePath { get; }

    public SqliteConnection Connection =>
        !_disposed ? _connection : throw new ObjectDisposedException(nameof(EngineeringGraphStore));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
    }

    private static void BackupBeforeMigration(SqliteConnection connection, string databasePath)
    {
        var backupPath = databasePath + ".bak";
        if (File.Exists(backupPath))
            File.Delete(backupPath);

        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $backupPath;";
        command.Parameters.AddWithValue("$backupPath", backupPath);
        command.ExecuteNonQuery();
    }
}
