using System.Text.Json;

namespace Contracts.Sandbox;

/// <summary>
/// Host-owned registry of workbench roots that were opened or created from persisted metadata.
/// MCP servers may read this file, but never accept trusted roots as tool arguments.
/// </summary>
public sealed class TrustedWorkbenchRootRegistry
{
    public const string EnvironmentVariableName = "AUTOMATION_WORKBENCH_TRUSTED_ROOTS_FILE";
    private readonly string path;
    private readonly object sync = new();

    public TrustedWorkbenchRootRegistry(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? DefaultFilePath);
    }

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AutomationWorkbench",
        "trusted-workbench-roots.json");

    public string FilePath => path;

    public void Register(string root)
    {
        var canonical = Path.GetFullPath(root);
        RejectReparseSegments(canonical);

        lock (sync)
        {
            var roots = Read()
                .Append(canonical)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var parent = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(parent);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new RegistryDocument(1, roots)));
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
    }

    public IReadOnlyList<string> Read()
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }

            var document = JsonSerializer.Deserialize<RegistryDocument>(File.ReadAllText(path));
            if (document?.SchemaVersion != 1 || document.Roots is null)
            {
                return Array.Empty<string>();
            }

            return document.Roots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(Path.GetFullPath)
                .Where(root =>
                {
                    try
                    {
                        RejectReparseSegments(root);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or NotSupportedException)
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

    private sealed class RegistryDocument
    {
        public RegistryDocument()
        {
        }

        public RegistryDocument(int schemaVersion, string[] roots)
        {
            SchemaVersion = schemaVersion;
            Roots = roots;
        }

        public int SchemaVersion { get; set; }
        public string[] Roots { get; set; } = Array.Empty<string>();
    }
}
