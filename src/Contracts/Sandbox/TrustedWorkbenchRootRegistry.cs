using System.Security.Cryptography;
using System.Security;
using System.Text;
using System.Text.Json;

namespace Contracts.Sandbox;

public sealed class TrustedWorkbenchRoot
{
    public TrustedWorkbenchRoot(string workbenchId, string rootPath)
    {
        WorkbenchId = workbenchId;
        RootPath = rootPath;
    }

    public string WorkbenchId { get; }
    public string RootPath { get; }
}

/// <summary>
/// Host-owned registry of identity-bound workbench roots. A grant is effective only while
/// workbench.json at that root still declares the same schema, id, and root path.
/// </summary>
public sealed class TrustedWorkbenchRootRegistry
{
    public const string EnvironmentVariableName = "AUTOMATION_WORKBENCH_TRUSTED_ROOTS_FILE";
    // 1.1 added optional landing-page fields (purpose/owner/status); 1.2 added the SVN native
    // store path and TIA project provenance fields; grants stay valid across both.
    private static readonly string[] SupportedWorkbenchSchemas = ["1.0", "1.1", "1.2"];
    private readonly string path;
    private readonly string mutexName;

    public TrustedWorkbenchRootRegistry(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? DefaultFilePath);
        mutexName = BuildMutexName(this.path);
    }

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AutomationWorkbench",
        "trusted-workbench-roots.json");

    public string FilePath => path;

    public void Register(string workbenchId, string root)
    {
        if (string.IsNullOrWhiteSpace(workbenchId))
        {
            throw new ArgumentException("Workbench id must not be empty.", nameof(workbenchId));
        }

        var candidate = ValidateGrant(new TrustedWorkbenchRoot(workbenchId, root));
        WithExclusiveLock(() =>
        {
            var entries = ReadDocumentEntries()
                .Where(IsGrantStillValid)
                .Where(entry => !string.Equals(
                    entry.WorkbenchId,
                    candidate.WorkbenchId,
                    StringComparison.Ordinal))
                .Append(candidate);
            WriteDocument(entries);
        });
    }

    /// <summary>
    /// Atomically merges the trusted host's current catalog snapshot while pruning invalid grants.
    /// The merge prevents concurrent host instances from revoking one another's still-valid roots.
    /// </summary>
    public void Reconcile(IEnumerable<TrustedWorkbenchRoot> roots)
    {
        if (roots is null)
        {
            throw new ArgumentNullException(nameof(roots));
        }

        var validated = roots.Select(ValidateGrant)
            .GroupBy(entry => entry.WorkbenchId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
        WithExclusiveLock(() =>
        {
            var current = ReadDocumentEntries().Where(IsGrantStillValid);
            WriteDocument(current
                .Concat(validated)
                .GroupBy(entry => entry.WorkbenchId, StringComparer.Ordinal)
                .Select(group => group.Last()));
        });
    }

    /// <summary>Returns only grants whose workbench metadata still matches. Any read error fails closed.</summary>
    public IReadOnlyList<string> Read()
    {
        try
        {
            return ReadDocumentEntries()
                .Where(IsGrantStillValid)
                .Select(entry => Path.GetFullPath(entry.RootPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (IsFailClosedException(exception))
        {
            return Array.Empty<string>();
        }
    }

    internal static void RejectReparseSegments(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var relative = full.Substring(root.Length);
        var current = root;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new SandboxException(
                    "SANDBOX_PATH_DENIED",
                    $"Path '{full}' traverses reparse point '{current}'.");
            }
        }
    }

    private RegistryEntry ValidateGrant(TrustedWorkbenchRoot grant)
    {
        var entry = new RegistryEntry
        {
            WorkbenchId = grant.WorkbenchId,
            RootPath = Path.GetFullPath(grant.RootPath),
        };
        if (!IsGrantStillValid(entry))
        {
            throw new SandboxException(
                "SANDBOX_PATH_DENIED",
                $"Workbench root '{entry.RootPath}' does not contain matching trusted metadata.");
        }

        return entry;
    }

    private static bool IsGrantStillValid(RegistryEntry entry)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(entry.WorkbenchId)
                || string.IsNullOrWhiteSpace(entry.RootPath))
            {
                return false;
            }

            var root = Path.GetFullPath(entry.RootPath);
            RejectReparseSegments(root);
            var metadataPath = Path.Combine(root, "workbench.json");
            if (!File.Exists(metadataPath)
                || (File.GetAttributes(metadataPath) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            var metadata = document.RootElement;
            var declaredRoot = StringProperty(metadata, "rootPath");
            return metadata.ValueKind == JsonValueKind.Object
                && StringProperty(metadata, "schemaVersion") is { } declaredSchema
                && SupportedWorkbenchSchemas.Contains(declaredSchema, StringComparer.Ordinal)
                && !string.IsNullOrWhiteSpace(declaredRoot)
                && string.Equals(
                    StringProperty(metadata, "workbenchId"),
                    entry.WorkbenchId,
                    StringComparison.Ordinal)
                && string.Equals(
                    Path.GetFullPath(declaredRoot!)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (IsFailClosedException(exception))
        {
            return false;
        }
    }

    private RegistryEntry[] ReadDocumentEntries()
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<RegistryEntry>();
            }

            var document = JsonSerializer.Deserialize<RegistryDocument>(File.ReadAllText(path));
            return document?.SchemaVersion == 1 && document.Roots is not null
                ? document.Roots
                : Array.Empty<RegistryEntry>();
        }
        catch (Exception exception) when (IsFailClosedException(exception))
        {
            return Array.Empty<RegistryEntry>();
        }
    }

    private void WriteDocument(IEnumerable<RegistryEntry> entries)
    {
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var document = new RegistryDocument
            {
                SchemaVersion = 1,
                Roots = entries
                    .OrderBy(entry => entry.WorkbenchId, StringComparer.Ordinal)
                    .ToArray(),
            };
            File.WriteAllText(temporary, JsonSerializer.Serialize(document));
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void WithExclusiveLock(Action action)
    {
        using var mutex = new Mutex(false, mutexName);
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
                throw new IOException("Timed out waiting for the trusted-root registry lock.");
            }

            action();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static string BuildMutexName(string registryPath)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(registryPath.ToUpperInvariant()));
        return "AutomationWorkbench.TrustedRoots." + BitConverter.ToString(hash).Replace("-", string.Empty);
    }

    private static string? StringProperty(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsFailClosedException(Exception exception) =>
        exception is IOException
        or UnauthorizedAccessException
        or JsonException
        or ArgumentException
        or NotSupportedException
        or SecurityException;

    private sealed class RegistryDocument
    {
        public int SchemaVersion { get; set; }
        public RegistryEntry[] Roots { get; set; } = Array.Empty<RegistryEntry>();
    }

    private sealed class RegistryEntry
    {
        public string WorkbenchId { get; set; } = string.Empty;
        public string RootPath { get; set; } = string.Empty;
    }
}
