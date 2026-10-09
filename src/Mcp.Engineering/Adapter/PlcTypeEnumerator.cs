using Siemens.Engineering.SW.Types;

namespace Mcp.Engineering.Adapter;

/// <summary>
/// Recursive UDT (PLC data type) enumeration incl. nested user groups (mirrors BlockEnumerator).
/// SystemTypeGroups (system/library types) are intentionally not enumerated — user UDTs only
/// (openness-v17-api-surface.md §9).
/// </summary>
internal static class PlcTypeEnumerator
{
    public static IEnumerable<(PlcType Type, string? GroupPath)> Enumerate(PlcTypeGroup root)
        => Walk(root, null);

    private static IEnumerable<(PlcType, string?)> Walk(PlcTypeGroup group, string? path)
    {
        foreach (PlcType type in group.Types)
            yield return (type, path);

        foreach (PlcTypeUserGroup sub in group.Groups)
        {
            var subPath = path is null ? sub.Name : path + "/" + sub.Name;
            foreach (var item in Walk(sub, subPath))
                yield return item;
        }
    }

    /// <summary>
    /// Finds one UDT by its manifest source path by descending only the groups that path names; see
    /// <see cref="BlockEnumerator.TryFindBySourcePath"/>. False means the caller must fall back to the
    /// complete enumeration.
    /// </summary>
    public static bool TryFindBySourcePath(
        PlcTypeGroup root,
        string? groupPath,
        string name,
        out PlcType type,
        out string? foundGroupPath)
    {
        var group = root;
        string? path = null;
        var segments = groupPath?.Split('/');
        if (segments is not null)
        {
            foreach (var segment in segments)
            {
                PlcTypeUserGroup? next = null;
                foreach (PlcTypeUserGroup candidate in group.Groups)
                {
                    if (string.Equals(candidate.Name, segment, StringComparison.Ordinal))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next is null)
                {
                    type = null!;
                    foundGroupPath = null;
                    return false;
                }

                group = next;
                path = path is null ? next.Name : path + "/" + next.Name;
            }
        }

        foreach (PlcType candidate in group.Types)
        {
            if (!string.Equals(candidate.Name, name, StringComparison.Ordinal))
                continue;

            type = candidate;
            foundGroupPath = path;
            return true;
        }

        type = null!;
        foundGroupPath = null;
        return false;
    }
}
