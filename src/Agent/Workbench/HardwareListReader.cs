using System.Xml.Linq;

namespace Agent.Workbench;

public sealed record HardwareBomView(
    string State,
    string? ExportedAt,
    IReadOnlyList<HardwareBomItem> Items,
    string? Message);

public sealed record HardwareBomItem(
    string Id,
    string Name,
    string Path,
    string Position,
    int? PositionNumber,
    string? TypeName,
    string TypeIdentifier,
    string? OrderNumber,
    string? FirmwareVersion);

public sealed record HardwareNetworkView(
    string State,
    string? ExportedAt,
    IReadOnlyList<HardwareNetworkNode> Nodes,
    string? Message);

public sealed record HardwareNetworkNode(
    string Id,
    string Address,
    string? SubnetMask,
    string? ProfinetDeviceName,
    string DeviceName,
    string DevicePath,
    string? InterfaceLabel,
    string? SubnetName);

/// <summary>
/// Derives flat list views (bill of materials, network nodes) from the exported project AML.
/// Shares the on-disk layout resolution with <see cref="HardwareConfigurationReader"/> via
/// <see cref="HardwareAml"/>.
/// </summary>
public static class HardwareListReader
{
    private const string OrderNumberPrefix = "OrderNumber:";

    public static HardwareBomView ReadBom(string worktreeRoot)
    {
        var source = HardwareAml.Resolve(worktreeRoot);
        if (source.State != "available" || source.Document is null)
        {
            return new HardwareBomView(source.State, source.ExportedAt, Array.Empty<HardwareBomItem>(), source.Message);
        }

        var items = source.Document
            .Descendants()
            .Where(element => Is(element, "InternalElement"))
            .Select(ReadBomItem)
            .Where(item => item is not null)
            .Cast<HardwareBomItem>()
            .ToArray();
        return new HardwareBomView(
            "available",
            source.ExportedAt,
            items,
            items.Length == 0 ? "The project AML contains no typed hardware components." : null);
    }

    public static HardwareNetworkView ReadNetwork(string worktreeRoot)
    {
        var source = HardwareAml.Resolve(worktreeRoot);
        if (source.State != "available" || source.Document is null)
        {
            return new HardwareNetworkView(source.State, source.ExportedAt, Array.Empty<HardwareNetworkNode>(), source.Message);
        }

        var document = source.Document;

        // Subnet InternalElements, keyed by their element ID. TIA marks them with the
        // AutomationProjectConfigurationRoleClassLib/Subnet supported role class.
        var subnetNameByElementId = document
            .Descendants()
            .Where(element => Is(element, "InternalElement")
                && element.Elements().Any(child => Is(child, "SupportedRoleClass")
                    && (AttributeValue(child, "RefRoleClassPath") ?? string.Empty)
                        .EndsWith("/Subnet", StringComparison.OrdinalIgnoreCase)))
            .Where(element => !string.IsNullOrWhiteSpace(AttributeValue(element, "ID")))
            .ToDictionary(
                element => AttributeValue(element, "ID")!,
                element => AttributeValue(element, "Name") ?? "Unnamed subnet",
                StringComparer.OrdinalIgnoreCase);

        // InternalLink partners reference the owning InternalElement, formatted as
        // "<element-guid>:<endpoint-name>" (e.g. "...:LogicalEndPoint_Node").
        // Node element ID -> subnet name.
        var nodeToSubnet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in document.Descendants().Where(element => Is(element, "InternalLink")))
        {
            var sideA = LinkPartner(AttributeValue(link, "RefPartnerSideA"));
            var sideB = LinkPartner(AttributeValue(link, "RefPartnerSideB"));
            if (sideA is null || sideB is null)
            {
                continue;
            }

            Match(sideA.Value, sideB.Value);
            Match(sideB.Value, sideA.Value);
        }

        var nodes = new List<HardwareNetworkNode>();
        foreach (var element in document.Descendants().Where(element => Is(element, "InternalElement")))
        {
            var properties = ReadAttributes(element);
            if (!properties.TryGetValue("NetworkAddress", out var address) || string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            // The device is the outermost typed ancestor (the station), not the nearest
            // typed component — interfaces often sit under typed submodules such as bus
            // adapters, which would otherwise masquerade as the device.
            var device = element
                .Ancestors()
                .Where(parent => Is(parent, "InternalElement"))
                .LastOrDefault(parent => ReadAttributes(parent).ContainsKey("TypeIdentifier"));
            var deviceName = AttributeValue(device ?? element, "Name") ?? "Unknown device";
            var devicePath = string.Join(
                ".",
                (device ?? element)
                    .Ancestors()
                    .Where(parent => Is(parent, "InternalElement"))
                    .Reverse()
                    .Select(parent => AttributeValue(parent, "Name"))
                    .Append(deviceName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))!);

            var interfaceElement = element.Parent;
            var interfaceLabel = interfaceElement is not null && Is(interfaceElement, "InternalElement")
                ? ReadAttributes(interfaceElement).TryGetValue("Label", out var label) ? label : null
                : null;

            string? subnetName = null;
            var nodeElementId = AttributeValue(element, "ID");
            if (nodeElementId is not null)
            {
                nodeToSubnet.TryGetValue(nodeElementId, out subnetName);
            }

            properties.TryGetValue("SubnetMask", out var subnetMask);
            properties.TryGetValue("ProfinetDeviceName", out var profinetDeviceName);
            nodes.Add(new HardwareNetworkNode(
                AttributeValue(element, "ID") ?? devicePath,
                NormalizeValue(address),
                string.IsNullOrWhiteSpace(subnetMask) ? null : NormalizeValue(subnetMask),
                string.IsNullOrWhiteSpace(profinetDeviceName) ? null : NormalizeValue(profinetDeviceName),
                deviceName,
                devicePath,
                string.IsNullOrWhiteSpace(interfaceLabel) ? null : NormalizeValue(interfaceLabel),
                subnetName));
        }

        return new HardwareNetworkView(
            "available",
            source.ExportedAt,
            nodes,
            nodes.Count == 0 ? "The project AML contains no addressed network nodes." : null);

        void Match((string Id, string Endpoint) node, (string Id, string Endpoint) other)
        {
            if (!string.Equals(node.Endpoint, "LogicalEndPoint_Node", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(other.Endpoint, "LogicalEndPoint_Subnet", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (subnetNameByElementId.TryGetValue(other.Id, out var subnetName))
            {
                nodeToSubnet[node.Id] = subnetName;
            }
        }
    }

    private static HardwareBomItem? ReadBomItem(XElement element)
    {
        var properties = ReadAttributes(element);
        if (!properties.TryGetValue("TypeIdentifier", out var typeIdentifier) || string.IsNullOrWhiteSpace(typeIdentifier))
        {
            return null;
        }

        typeIdentifier = NormalizeValue(typeIdentifier);
        var orderNumber = typeIdentifier.StartsWith(OrderNumberPrefix, StringComparison.OrdinalIgnoreCase)
            ? typeIdentifier[OrderNumberPrefix.Length..].Trim()
            : null;
        properties.TryGetValue("TypeName", out var typeName);
        properties.TryGetValue("FirmwareVersion", out var firmwareVersion);
        var positionNumber = properties.TryGetValue("PositionNumber", out var positionText)
            && int.TryParse(positionText, out var parsedPosition)
                ? parsedPosition
                : (int?)null;

        var name = AttributeValue(element, "Name") ?? "Unnamed component";
        var ancestors = element
            .Ancestors()
            .Where(parent => Is(parent, "InternalElement"))
            .Reverse()
            .Select(parent => AttributeValue(parent, "Name"))
            .Where(ancestorName => !string.IsNullOrWhiteSpace(ancestorName))
            .ToArray();
        var position = string.Join(" / ", ancestors!);
        var path = ancestors.Length == 0 ? name : $"{string.Join(".", ancestors!)}.{name}";

        return new HardwareBomItem(
            AttributeValue(element, "ID") ?? path,
            name,
            path,
            position,
            positionNumber,
            string.IsNullOrWhiteSpace(typeName) ? null : NormalizeValue(typeName),
            typeIdentifier,
            orderNumber,
            string.IsNullOrWhiteSpace(firmwareVersion) ? null : NormalizeValue(firmwareVersion));
    }

    /// <summary>Reads the direct <c>Attribute</c> children of an element into a name/value map.</summary>
    private static Dictionary<string, string> ReadAttributes(XElement element)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in element.Elements().Where(child => Is(child, "Attribute")))
        {
            var name = AttributeValue(attribute, "Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var value = attribute
                .Elements()
                .FirstOrDefault(child => Is(child, "Value"))
                ?.Value
                .Trim();
            if (value is not null)
            {
                values[name] = value;
            }
        }

        return values;
    }

    /// <summary>
    /// Splits an InternalLink partner reference ("<c>&lt;element-guid&gt;:&lt;endpoint-name&gt;</c>")
    /// into its parts. Returns null when the id segment is empty.
    /// </summary>
    private static (string Id, string Endpoint)? LinkPartner(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var separator = reference.IndexOf(':');
        var id = separator < 0 ? reference : reference[..separator];
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var endpoint = separator < 0 ? string.Empty : reference[(separator + 1)..];
        return (id.Trim(), endpoint.Trim());
    }

    private static string NormalizeValue(string value)
    {
        var normalized = value.Trim();
        return normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"'
            ? normalized[1..^1]
            : normalized;
    }

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool Is(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);
}
