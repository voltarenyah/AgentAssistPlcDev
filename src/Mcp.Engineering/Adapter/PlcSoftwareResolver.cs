using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace Mcp.Engineering.Adapter;

/// <summary>
/// Device-tree traversal (mcp-engineering.md §13.4; namespaces verified 2026-07-17):
/// Project.Devices → HardwareObject.DeviceItems (recursive) → GetService&lt;SoftwareContainer&gt;()
/// → Software as PlcSoftware.
/// </summary>
internal static class PlcSoftwareResolver
{
    public static IReadOnlyList<PlcSoftware> FindAll(Project project)
    {
        var result = new List<PlcSoftware>();
        foreach (Device device in project.Devices)
            Collect(device, result);
        return result;
    }

    private static void Collect(HardwareObject node, List<PlcSoftware> result)
    {
        if (node is DeviceItem item
            && item.GetService<SoftwareContainer>()?.Software is PlcSoftware plc)
        {
            result.Add(plc);
        }
        foreach (DeviceItem child in node.DeviceItems)
            Collect(child, result);
    }

    /// <summary>Single-PLC projects resolve without a name; multi-PLC projects require plcName.</summary>
    public static PlcSoftware Resolve(Project project, string? plcName)
    {
        var all = FindAll(project);
        if (plcName is not null)
        {
            var match = all.FirstOrDefault(p =>
                string.Equals(p.Name, plcName, StringComparison.OrdinalIgnoreCase));
            return match ?? throw new AdapterException("PLC_NOT_FOUND",
                $"No PLC named '{plcName}' in the project.", Available(all));
        }

        return all.Count switch
        {
            1 => all[0],
            0 => throw new AdapterException("PLC_NOT_FOUND", "No PLC software found in the project."),
            _ => throw new AdapterException("AMBIGUOUS_PLC",
                $"Project has {all.Count} PLC devices; specify plcName.", Available(all)),
        };
    }

    private static string Available(IReadOnlyList<PlcSoftware> all) =>
        "Available PLCs: " + string.Join(", ", all.Select(p => p.Name));
}
