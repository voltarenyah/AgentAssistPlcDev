using Siemens.Engineering.SW.Blocks;

namespace Mcp.Engineering.Adapter;

/// <summary>
/// Recursive block enumeration incl. nested block groups (mcp-engineering.md §5.3).
/// Group nesting uses PlcBlockUserGroupComposition (verified: no PlcBlockGroupComposition exists).
/// </summary>
internal static class BlockEnumerator
{
    public static IEnumerable<(PlcBlock Block, string? GroupPath)> Enumerate(PlcBlockGroup root)
        => Walk(root, null);

    private static IEnumerable<(PlcBlock, string?)> Walk(PlcBlockGroup group, string? path)
    {
        foreach (PlcBlock block in group.Blocks)
            yield return (block, path);

        foreach (PlcBlockUserGroup sub in group.Groups)
        {
            var subPath = path is null ? sub.Name : path + "/" + sub.Name;
            foreach (var item in Walk(sub, subPath))
                yield return item;
        }
    }

    public static PlcBlock Find(PlcBlockGroup root, string blockName) =>
        FindWithPath(root, blockName).Block;

    /// <summary>
    /// Finds one block by its manifest source path — the '/'-joined user-group path plus the block
    /// name — by descending only the groups that path names, so a targeted read costs what the request
    /// contains rather than what the project contains. Returns false when no such group or block
    /// exists, and the caller then falls back to the complete enumeration instead of guessing.
    /// </summary>
    public static bool TryFindBySourcePath(
        PlcBlockGroup root,
        string? groupPath,
        string name,
        out PlcBlock block,
        out string? foundGroupPath)
    {
        var group = root;
        string? path = null;
        var segments = groupPath?.Split('/');
        if (segments is not null)
        {
            foreach (var segment in segments)
            {
                PlcBlockUserGroup? next = null;
                foreach (PlcBlockUserGroup candidate in group.Groups)
                {
                    if (string.Equals(candidate.Name, segment, StringComparison.Ordinal))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next is null)
                {
                    block = null!;
                    foundGroupPath = null;
                    return false;
                }

                group = next;
                path = path is null ? next.Name : path + "/" + next.Name;
            }
        }

        foreach (PlcBlock candidate in group.Blocks)
        {
            if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
                continue;

            block = candidate;
            foundGroupPath = path;
            return true;
        }

        block = null!;
        foundGroupPath = null;
        return false;
    }

    /// <summary>Find including the block's group path (export manifests key records by source path).</summary>
    public static (PlcBlock Block, string? GroupPath) FindWithPath(PlcBlockGroup root, string blockName)
    {
        var matches = Enumerate(root)
            .Where(x => string.Equals(x.Block.Name, blockName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new AdapterException("BLOCK_NOT_FOUND",
                $"Block '{blockName}' not found.", "Call list_blocks to see available blocks."),
            _ => throw new AdapterException("AMBIGUOUS_BLOCK",
                $"Multiple blocks named '{blockName}' exist in different block groups."),
        };
    }
}
