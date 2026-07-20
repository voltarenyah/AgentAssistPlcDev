using System.Text.Json;

namespace Contracts.Sandbox;

/// <summary>
/// Sandbox configuration, loaded from %APPDATA%\PlcAiAssistant\sandbox.json (missing/invalid
/// file → built-in defaults, same tolerance as config.json). Shape:
/// { "tiers": { "&lt;tool&gt;": "read|write|destructive|deny" }, "allowedRoots": ["…"],
///   "maxDestructiveCallsPerSession": 20, "auditDirectory": "…" }.
/// allowedRoots EXTENDS the default roots; unrecognized tier values fail closed (deny).
/// </summary>
public sealed class SandboxConfig
{
    public const int DefaultMaxDestructiveCallsPerSession = 20;

    private SandboxConfig(
        SandboxPolicy policy,
        PathJail pathJail,
        int maxDestructiveCallsPerSession,
        string auditDirectory,
        string sourceDescription)
    {
        Policy = policy;
        PathJail = pathJail;
        MaxDestructiveCallsPerSession = maxDestructiveCallsPerSession;
        AuditDirectory = auditDirectory;
        SourceDescription = sourceDescription;
    }

    public SandboxPolicy Policy { get; }

    public PathJail PathJail { get; }

    /// <summary>Destructive executions the agent may perform per session; 0 blocks them all.</summary>
    public int MaxDestructiveCallsPerSession { get; }

    public string AuditDirectory { get; }

    /// <summary>Where the effective config came from (file path or "built-in defaults").</summary>
    public string SourceDescription { get; }

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PlcAiAssistant",
        "sandbox.json");

    public static string DefaultAuditDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlcAiAssistant",
        "audit");

    /// <summary>%LOCALAPPDATA%\PlcAiAssistant (exports, knowledge dbs) + the TIA default project dir.</summary>
    public static IReadOnlyList<string> DefaultRoots { get; } = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlcAiAssistant"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation"),
    };

    public static SandboxConfig LoadDefault() => Load(DefaultFilePath);

    public static SandboxConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Defaults("built-in defaults (no sandbox.json)");
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            var overrides = new Dictionary<string, SandboxTier>(StringComparer.Ordinal);
            if (root.TryGetProperty("tiers", out var tiersElement) && tiersElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in tiersElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        overrides[property.Name] = SandboxTierNames.Parse(property.Value.GetString()!);
                    }
                }
            }

            var roots = new List<string>(DefaultRoots);
            if (root.TryGetProperty("allowedRoots", out var rootsElement) && rootsElement.ValueKind == JsonValueKind.Array)
            {
                roots.AddRange(rootsElement.EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => element.GetString()!));
            }

            var budget = root.TryGetProperty("maxDestructiveCallsPerSession", out var budgetElement)
                && budgetElement.ValueKind == JsonValueKind.Number
                && budgetElement.TryGetInt32(out var parsed)
                    ? Math.Max(0, parsed)
                    : DefaultMaxDestructiveCallsPerSession;

            var auditDirectory = root.TryGetProperty("auditDirectory", out var auditElement)
                && auditElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(auditElement.GetString())
                    ? auditElement.GetString()!
                    : DefaultAuditDirectory;

            return new SandboxConfig(
                new SandboxPolicy(overrides),
                new PathJail(roots),
                budget,
                auditDirectory,
                path);
        }
        catch (JsonException)
        {
            return Defaults($"built-in defaults (invalid JSON in {path})");
        }
    }

    private static SandboxConfig Defaults(string sourceDescription) => new(
        new SandboxPolicy(),
        new PathJail(DefaultRoots),
        DefaultMaxDestructiveCallsPerSession,
        DefaultAuditDirectory,
        sourceDescription);
}
