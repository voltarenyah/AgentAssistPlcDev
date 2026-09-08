using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agent.Workbench;

public sealed class WorkbenchTagStoreException : Exception
{
    public WorkbenchTagStoreException(string message)
        : base(message)
    {
    }

    public WorkbenchTagStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Durable, application-global tag document storage. All read-modify-write operations are
/// serialized per path and written through the repository's atomic JSON writer.
/// </summary>
public sealed class WorkbenchTagStore
{
    public const string CurrentSchemaVersion = "1.0";

    private readonly string _path;
    private readonly string _mutexName;
    private readonly AtomicJsonStore _jsonStore;

    public WorkbenchTagStore(string? path = null, AtomicJsonStore? jsonStore = null)
    {
        _path = Path.GetFullPath(path ?? DefaultFilePath);
        _mutexName = BuildMutexName(_path);
        _jsonStore = jsonStore ?? new AtomicJsonStore();
    }

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutomationWorkbench",
        "metadata",
        "tags.json");

    public string FilePath => _path;

    public WorkbenchTagDocument Load() => WithExclusiveLock(ReadDocument);

    public void Save(WorkbenchTagDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        WithExclusiveLock(() => WriteDocument(document));
    }

    /// <summary>Applies one mutation while holding the same lock used for loading and saving.</summary>
    public WorkbenchTagDocument Mutate(Func<WorkbenchTagDocument, WorkbenchTagDocument> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return WithExclusiveLock(() =>
        {
            var updated = mutation(ReadDocument());
            if (updated is null)
            {
                throw new WorkbenchTagStoreException("A tag document mutation returned null.");
            }

            WriteDocument(updated);
            return updated;
        });
    }

    private WorkbenchTagDocument ReadDocument()
    {
        if (!File.Exists(_path))
        {
            return EmptyDocument();
        }

        try
        {
            using var stream = File.OpenRead(_path);
            using var json = JsonDocument.Parse(stream);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.String)
            {
                throw new WorkbenchTagStoreException(
                    $"Tag document '{_path}' does not declare schemaVersion '{CurrentSchemaVersion}'.");
            }

            var actualVersion = schema.GetString();
            if (!string.Equals(actualVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            {
                throw new WorkbenchTagStoreException(
                    $"Tag document schemaVersion '{actualVersion}' is not supported; expected '{CurrentSchemaVersion}'.");
            }

            var document = _jsonStore.Read<WorkbenchTagDocument>(_path);
            if (document.Nodes is null || document.Assignments is null)
            {
                throw new WorkbenchTagStoreException(
                    $"Tag document '{_path}' must contain nodes and assignments arrays.");
            }

            return document;
        }
        catch (WorkbenchTagStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            throw new WorkbenchTagStoreException(
                $"Tag document '{_path}' could not be read.",
                exception);
        }
    }

    private void WriteDocument(WorkbenchTagDocument document)
    {
        if (!string.Equals(document.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new WorkbenchTagStoreException(
                $"Tag document schemaVersion '{document.SchemaVersion}' is not supported; expected '{CurrentSchemaVersion}'.");
        }

        if (document.Nodes is null || document.Assignments is null)
        {
            throw new WorkbenchTagStoreException(
                "A tag document must contain nodes and assignments arrays.");
        }

        try
        {
            _jsonStore.Write(_path, document);
        }
        catch (WorkbenchTagStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or JsonException)
        {
            throw new WorkbenchTagStoreException(
                $"Tag document '{_path}' could not be written.",
                exception);
        }
    }

    private static WorkbenchTagDocument EmptyDocument() =>
        new(CurrentSchemaVersion, [], []);

    private T WithExclusiveLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, _mutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new WorkbenchTagStoreException(
                    "Timed out waiting for the tag document lock.");
            }

            return action();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private void WithExclusiveLock(Action action) =>
        WithExclusiveLock(() =>
        {
            action();
            return true;
        });

    private static string BuildMutexName(string path)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return "AutomationWorkbench.WorkbenchTags." +
            BitConverter.ToString(hash).Replace("-", string.Empty);
    }
}
