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

    /// <summary>
    /// Finds one tag table by its manifest source path by descending only the groups that path names;
    /// see <see cref="BlockEnumerator.TryFindBySourcePath"/>. False means the caller must fall back to
    /// the complete enumeration.
    /// </summary>
    public static bool TryFindBySourcePath(
        PlcTagTableGroup root,
        string? groupPath,
        string name,
        out PlcTagTable table,
        out string? foundGroupPath)
    {
        var group = root;
        string? path = null;
        var segments = groupPath?.Split('/');
        if (segments is not null)
        {
            foreach (var segment in segments)
            {
                PlcTagTableUserGroup? next = null;
                foreach (PlcTagTableUserGroup candidate in group.Groups)
                {
                    if (string.Equals(candidate.Name, segment, StringComparison.Ordinal))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next is null)
                {
                    table = null!;
                    foundGroupPath = null;
                    return false;
                }

                group = next;
                path = path is null ? next.Name : path + "/" + next.Name;
            }
        }

        foreach (PlcTagTable candidate in group.TagTables)
        {
            if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
                continue;

            table = candidate;
            foundGroupPath = path;
            return true;
        }

        table = null!;
        foundGroupPath = null;
        return false;
    }
}
