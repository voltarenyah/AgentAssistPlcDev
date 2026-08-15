namespace Contracts.Sandbox;

/// <summary>
/// Tool → tier map with built-in defaults for every tool of the current MCP servers
/// (engineering + knowledge + versioncontrol). Config overrides merge on top; a tool
/// missing from the map classifies as null and must be denied by callers (fail closed —
/// a newly added tool is blocked until someone classifies it).
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

    /// <summary>Built-in tiers for tools exposed by the current MCP servers.</summary>
    public static IReadOnlyDictionary<string, SandboxTier> Defaults { get; } =
        new Dictionary<string, SandboxTier>(StringComparer.Ordinal)
        {
            // Engineering — read-only w.r.t. the TIA project (exports write only under outputDir, path-jailed).
            ["check_environment"] = SandboxTier.Read,
            ["list_sessions"] = SandboxTier.Read,
            ["get_current_session"] = SandboxTier.Read,
            ["get_project_info"] = SandboxTier.Read,
            ["get_project_capabilities"] = SandboxTier.Read,
            ["list_blocks"] = SandboxTier.Read,
            ["get_plc_checksums"] = SandboxTier.Read,
            ["export_block"] = SandboxTier.Read,
            ["export_all_blocks"] = SandboxTier.Read,
            ["export_tag_tables"] = SandboxTier.Read,
            ["export_udts"] = SandboxTier.Read,
            ["sync_export"] = SandboxTier.Read,
            ["rebuild_export"] = SandboxTier.Read,
            ["export_hardware_configuration"] = SandboxTier.Read,
            ["get_context_status"] = SandboxTier.Read,
            ["compare_context"] = SandboxTier.Read,
            // Engineering — mutate project/portal state but do not persist or overwrite user code.
            ["connect"] = SandboxTier.Write,
            ["disconnect"] = SandboxTier.Write,
            ["close_session"] = SandboxTier.Write,
            ["compile_block"] = SandboxTier.Write,
            ["compile_plc"] = SandboxTier.Write,
            ["open_block_in_editor"] = SandboxTier.Write,
            // Engineering — persist/overwrite user work.
            ["save_project"] = SandboxTier.Destructive,
            ["save_project_as"] = SandboxTier.Destructive,
            ["create_project"] = SandboxTier.Write,
            ["archive_project"] = SandboxTier.Write,
            ["retrieve_project"] = SandboxTier.Write,
            ["import_block"] = SandboxTier.Destructive,
            ["import_hardware_configuration"] = SandboxTier.Destructive,
            ["create_block"] = SandboxTier.Write,
            ["delete_block"] = SandboxTier.Destructive,
            ["import_source_object"] = SandboxTier.Destructive,
            // Knowledge — local SQLite graph only; no TIA side effects.
            ["ingest_source"] = SandboxTier.Read,
            ["update_components"] = SandboxTier.Write,
            ["query"] = SandboxTier.Read,
            ["get_schema"] = SandboxTier.Read,
            ["get_block"] = SandboxTier.Read,
            ["get_network"] = SandboxTier.Read,
            ["get_single_network"] = SandboxTier.Read,
            ["get_all_networks"] = SandboxTier.Read,
            ["get_variable_usage"] = SandboxTier.Read,
            ["search"] = SandboxTier.Read,
            ["query_node_kinds"] = SandboxTier.Read,
            ["query_nodes"] = SandboxTier.Read,
            ["query_edge_types"] = SandboxTier.Read,
            ["query_edges"] = SandboxTier.Read,
            ["query_node_properties"] = SandboxTier.Read,
            ["query_edge_properties"] = SandboxTier.Read,
            // Source editor — inspection and comparison.
            ["src_parse_block"] = SandboxTier.Read,
            ["src_diff"] = SandboxTier.Read,
            ["src_validate"] = SandboxTier.Read,
            // Source editor — creates or replaces local XML under jailed roots.
            ["src_apply_edits"] = SandboxTier.Write,
            // Version control — read-only queries (status, log, diff, branches).
            ["vc_status"] = SandboxTier.Read,
            ["vc_log"] = SandboxTier.Read,
            ["vc_diff"] = SandboxTier.Read,
            ["vc_branches"] = SandboxTier.Read,
            ["vc_worktrees"] = SandboxTier.Read,
            // Version control — write operations (state changes).
            ["vc_init"] = SandboxTier.Write,
            ["vc_init_shared"] = SandboxTier.Write,
            ["vc_add_worktree"] = SandboxTier.Write,
            ["vc_merge"] = SandboxTier.Write,
            ["vc_merge_preview"] = SandboxTier.Read,
            ["vc_merge_validated"] = SandboxTier.Write,
            ["vc_apply_historical_paths"] = SandboxTier.Write,
            ["vc_add"] = SandboxTier.Write,
            ["vc_commit"] = SandboxTier.Write,
            ["vc_snapshot"] = SandboxTier.Write,
            ["vc_config"] = SandboxTier.Write,
            // Version control — destructive (overwrites working tree).
            ["vc_restore"] = SandboxTier.Destructive,
        };

    public IReadOnlyDictionary<string, SandboxTier> Tiers => tiers;

    /// <summary>Tier of the tool, or null when the policy does not know it (callers must deny).</summary>
    public SandboxTier? Classify(string toolName) =>
        tiers.TryGetValue(toolName, out var tier) ? tier : null;
}
