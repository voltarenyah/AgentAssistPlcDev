using Siemens.Engineering.SW.Tags;

namespace Mcp.Engineering.Adapter;

/// <summary>
/// Recursive tag-table enumeration incl. nested user groups (mirrors BlockEnumerator).
/// PlcTagTableSystemGroup adds nothing relevant for enumeration (openness-v17-api-surface.md §9).
/// </summary>
internal static class TagTableEnumerator
{
    public static IEnumerable<(PlcTagTable Table, string? GroupPath)> Enumerate(PlcTagTableGroup root)
        => Walk(root, null);

    private static IEnumerable<(PlcTagTable, string?)> Walk(PlcTagTableGroup group, string? path)
    {
        foreach (PlcTagTable table in group.TagTables)
            yield return (table, path);

        foreach (PlcTagTableUserGroup sub in group.Groups)
        {
            var subPath = path is null ? sub.Name : path + "/" + sub.Name;
            foreach (var item in Walk(sub, subPath))
                yield return item;
        }
    }
}
