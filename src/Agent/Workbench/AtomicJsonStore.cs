using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Workbench;

public sealed class MetadataSchemaException : Exception
{
    public MetadataSchemaException(string? actualVersion)
        : base(
            actualVersion is null
                ? $"Metadata does not declare schemaVersion '{WorkbenchSchema.CurrentVersion}'."
                : $"Metadata schemaVersion '{actualVersion}' is not supported; expected '{WorkbenchSchema.CurrentVersion}'.")
    {
        ActualVersion = actualVersion;
    }

    public string? ActualVersion { get; }
}

public sealed class AtomicJsonStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public T Read<T>(string path)
    {
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        ValidateSchema<T>(document.RootElement);

        return document.RootElement.Deserialize<T>(Json)
            ?? throw new JsonException(
                $"Metadata file '{path}' did not contain a {typeof(T).Name} value.");
    }

    public T? TryRead<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        return Read<T>(path);
    }

    public void Write<T>(string path, T value)
    {
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException(
                "A metadata path must include a parent directory.",
                nameof(path));

        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(destination))
            {
                File.Replace(
                    temporaryPath,
                    destination,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, destination);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateSchema<T>(JsonElement root)
    {
        if (!IsMetadataModel(typeof(T)))
        {
            return;
        }

        string? actualVersion = null;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("schemaVersion", out var schema)
            && schema.ValueKind == JsonValueKind.String)
        {
            actualVersion = schema.GetString();
        }

        if (!string.Equals(
                actualVersion,
                WorkbenchSchema.CurrentVersion,
                StringComparison.Ordinal))
        {
            throw new MetadataSchemaException(actualVersion);
        }
    }

    private static bool IsMetadataModel(Type type) =>
        type == typeof(WorkbenchMetadata)
        || type == typeof(WorktreeMetadata)
        || type == typeof(DeviceMetadata);
}
