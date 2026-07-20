namespace Contracts.Sandbox;

/// <summary>
/// Tool → tier map with built-in defaults for every tool of the current MCP servers
/// (engineering + knowledge). Config overrides merge on top; a tool missing from the map
/// classifies as null and must be denied by callers (fail closed — a newly added tool is
/// blocked until someone classifies it).
/// </summary>
public sealed class SandboxPolicy
{
    private readonly Dictionary<string, SandboxTier> tiers;

    public SandboxPolicy(IReadOnlyDictionary<string, SandboxTier>? overrides = null)
    {
        tiers = new Dictionary<string, SandboxTier>(StringComparer.Ordinal);
        foreach (var source in new[] { Defaults, overrides })
        {
            if (source == null)
            {
                continue;
            }

            foreach (var pair in source)
            {
                tiers[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>Built-in tiers: 21 tools (15 read, 4 write, 2 destructive).</summary>
    public static IReadOnlyDictionary<string, SandboxTier> Defaults { get; } =
        new Dictionary<string, SandboxTier>(StringComparer.Ordinal)
        {
            // Engineering — read-only w.r.t. the TIA project (exports write only under outputDir, path-jailed).
            ["check_environment"] = SandboxTier.Read,
            ["list_sessions"] = SandboxTier.Read,
            ["get_project_info"] = SandboxTier.Read,
            ["list_blocks"] = SandboxTier.Read,
            ["export_block"] = SandboxTier.Read,
            ["export_all_blocks"] = SandboxTier.Read,
            ["export_tag_tables"] = SandboxTier.Read,
            ["export_udts"] = SandboxTier.Read,
            ["sync_export"] = SandboxTier.Read,
            // Engineering — mutate project/portal state but do not persist or overwrite user code.
            ["connect"] = SandboxTier.Write,
            ["disconnect"] = SandboxTier.Write,
            ["compile_block"] = SandboxTier.Write,
            ["compile_plc"] = SandboxTier.Write,
            // Engineering — persist/overwrite user work.
            ["save_project"] = SandboxTier.Destructive,
            ["import_block"] = SandboxTier.Destructive,
            // Knowledge — local SQLite graph only; no TIA side effects.
            ["ingest_source"] = SandboxTier.Read,
            ["query"] = SandboxTier.Read,
            ["get_schema"] = SandboxTier.Read,
            ["get_block"] = SandboxTier.Read,
            ["get_network"] = SandboxTier.Read,
            ["search"] = SandboxTier.Read,
        };

    public IReadOnlyDictionary<string, SandboxTier> Tiers => tiers;

    /// <summary>Tier of the tool, or null when the policy does not know it (callers must deny).</summary>
    public SandboxTier? Classify(string toolName) =>
        tiers.TryGetValue(toolName, out var tier) ? tier : null;
}
