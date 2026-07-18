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
}
