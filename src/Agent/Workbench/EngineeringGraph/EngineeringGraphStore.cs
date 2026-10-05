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
            ConfigureConcurrency(_connection);
            var currentVersion = EngineeringGraphSchema.GetVersion(_connection);
            if (currentVersion > EngineeringGraphSchema.CurrentVersion)
            {
                throw new InvalidOperationException(
                    $"Engineering graph schema version '{currentVersion}' is newer than supported version '{EngineeringGraphSchema.CurrentVersion}'.");
            }

            if (currentVersion < EngineeringGraphSchema.CurrentVersion)
            {
                BackupBeforeMigration(_connection, DatabasePath);
                // The migration transaction is opened only when the ladder has to run. BeginTransaction
                // issues BEGIN IMMEDIATE, so opening it unconditionally made every graph scope take the
                // writer lock at construction — and the task page opens several scopes per interaction,
                // so a concurrent one waited out the driver's retry window and failed with "database is
                // locked". A current schema needs no write here at all.
                using var transaction = _connection.BeginTransaction();
                EngineeringGraphSchema.Apply(_connection, transaction, migrationFailureInjector);
                transaction.Commit();
            }
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

    /// <summary>
    /// Every request opens its own connection, so two are open at once whenever the task page's
    /// parallel reads — its stage lists and the device snapshot — overlap a stage write. SQLite raises
    /// SQLITE_BUSY for the second writer, and with no busy handler installed a blocked statement is
    /// only retried by the driver until its command timeout, which the user sees as a frozen page
    /// followed by "database is locked". Waiting briefly lets the other statement finish instead. The
    /// journal mode is deliberately left alone: switching to WAL would change the workbench's on-disk
    /// file set and would make "a failed migration leaves the database byte-identical" false.
    /// </summary>
    private static void ConfigureConcurrency(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
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
