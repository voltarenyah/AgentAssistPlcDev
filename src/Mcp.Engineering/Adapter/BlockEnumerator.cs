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

    public static PlcBlock Find(PlcBlockGroup root, string blockName)
    {
        var matches = Enumerate(root)
            .Where(x => string.Equals(x.Block.Name, blockName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return matches.Count switch
        {
            1 => matches[0].Block,
            0 => throw new AdapterException("BLOCK_NOT_FOUND",
                $"Block '{blockName}' not found.", "Call list_blocks to see available blocks."),
            _ => throw new AdapterException("AMBIGUOUS_BLOCK",
                $"Multiple blocks named '{blockName}' exist in different block groups."),
        };
    }
}
