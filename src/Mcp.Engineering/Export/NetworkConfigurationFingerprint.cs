using System.Globalization;
using System.Text;
using Contracts.Engineering;
using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.OpcUa;

namespace Mcp.Engineering.Export;

/// <summary>
/// Canonical, git-diff-friendly serialization of everything TIA Openness V17 can read about the
/// communication/network configuration (issue #69, docs/tiasoftwarechecksumblindpoints.md §3/§9):
/// subnets and MRP domains, per-device interface/node/port attributes (incl. PROFINET device
/// name and IP), IO-system assignments, port-to-port topology, and OPC UA server interfaces.
/// The SHA-256 of this text is stored in hardware/manifest.json (networkConfigurationHash) and
/// re-computed on compare; any covered edit flips the hardware consistency state.
/// S7/TCP/UDP connection parameters are NOT readable in V17 (the assembly has no Connections
/// API — buildnote/bestpractice/openness-v17-api-surface.md §11) and stay invisible.
/// </summary>
internal sealed class NetworkConfigurationFingerprint
{
    /// <summary>Artifact file name written next to project.aml in the hardware root.</summary>
    public const string FileName = "network-configuration.txt";

    private const string Header = "network-configuration-fingerprint/v1";

    private readonly SortedSet<string> _subnets = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _interfaces = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _ioAssignments = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _topology = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _opcUa = new(StringComparer.Ordinal);

    /// <summary>Reads the live project via Openness. Best-effort per object: a single unreadable
    /// interface or attribute is skipped (logged), never fails the capture.</summary>
    public static NetworkConfigurationFingerprint Capture(
        Project project,
        IEnumerable<Device> devices,
        IEnumerable<PlcSoftware> plcs,
        ILogger logger)
    {
        var fingerprint = new NetworkConfigurationFingerprint();
        fingerprint.CaptureSubnets(project, logger);
        foreach (var device in devices)
        {
            try
            {
                fingerprint.CaptureDeviceItems(device.Name, device.DeviceItems, logger);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "network fingerprint: skipping device {Device}", device.Name);
            }
        }
        foreach (var plc in plcs)
        {
            try
            {
                fingerprint.CaptureOpcUa(plc, logger);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "network fingerprint: skipping OPC UA of {Plc}", plc.Name);
            }
        }
        return fingerprint;
    }

    /// <summary>Deterministic text: fixed section order, every section ordinal-sorted, LF endings.</summary>
    public string Serialize()
    {
        var builder = new StringBuilder();
        builder.Append(Header).Append('\n');
        AppendSection(builder, "[subnets]", _subnets);
        AppendSection(builder, "[interfaces]", _interfaces);
        AppendSection(builder, "[io-assignments]", _ioAssignments);
        AppendSection(builder, "[topology]", _topology);
        AppendSection(builder, "[opcua]", _opcUa);
        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string header, SortedSet<string> lines)
    {
        builder.Append(header).Append('\n');
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }
    }

    // --- section writers (also the pure, unit-testable surface) ---

    internal void AddSubnet(string name, string netType, string? typeIdentifier) =>
        _subnets.Add($"subnet|{Field(name)}|{Field(netType)}|{Field(typeIdentifier)}");

    internal void AddSubnetNode(string subnetName, string nodeName) =>
        _subnets.Add($"subnet-node|{Field(subnetName)}|{Field(nodeName)}");

    internal void AddSubnetIoSystem(string subnetName, string systemName, int number) =>
        _subnets.Add($"subnet-iosystem|{Field(subnetName)}|{Field(systemName)}|{number.ToString(CultureInfo.InvariantCulture)}");

    internal void AddMrpDomain(string subnetName, string domainName, IReadOnlyList<KeyValuePair<string, string>> attributes)
    {
        _subnets.Add($"mrp-domain|{Field(subnetName)}|{Field(domainName)}");
        foreach (var attribute in attributes)
        {
            _subnets.Add($"mrp-domain-attr|{Field(subnetName)}|{Field(domainName)}|{Field(attribute.Key)}={Field(attribute.Value)}");
        }
    }

    internal void AddMrpParticipant(string subnetName, string domainName, string participant) =>
        _subnets.Add($"mrp-participant|{Field(subnetName)}|{Field(domainName)}|{Field(participant)}");

    internal void AddInterface(string deviceName, string itemPath, string interfaceType, string operatingMode) =>
        _interfaces.Add($"interface|{Field(deviceName)}|{Field(itemPath)}|{Field(interfaceType)}|{Field(operatingMode)}");

    internal void AddInterfaceAttribute(string deviceName, string itemPath, string name, string value) =>
        _interfaces.Add($"interface-attr|{Field(deviceName)}|{Field(itemPath)}|{Field(name)}={Field(value)}");

    internal void AddNode(string deviceName, string itemPath, string nodeName, string nodeType, string? nodeId, string? subnetName) =>
        _interfaces.Add($"node|{Field(deviceName)}|{Field(itemPath)}|{Field(nodeName)}|{Field(nodeType)}|{Field(nodeId)}|subnet={Field(subnetName)}");

    internal void AddNodeAttribute(string deviceName, string itemPath, string nodeName, string name, string value) =>
        _interfaces.Add($"node-attr|{Field(deviceName)}|{Field(itemPath)}|{Field(nodeName)}|{Field(name)}={Field(value)}");

    internal void AddIoController(string deviceName, string itemPath, string? ioSystemName, int? ioSystemNumber) =>
        _ioAssignments.Add($"io-controller|{Field(deviceName)}|{Field(itemPath)}|system={Field(ioSystemName)}|number={(ioSystemNumber?.ToString(CultureInfo.InvariantCulture) ?? "-")}");

    internal void AddIoConnector(string deviceName, string itemPath, string? ioSystemName) =>
        _ioAssignments.Add($"io-connector|{Field(deviceName)}|{Field(itemPath)}|system={Field(ioSystemName) ?? "-"}");

    /// <summary>Port-to-port link; endpoint order is normalized so the pair is recorded once.</summary>
    internal void AddTopologyLink(string endpointA, string endpointB)
    {
        if (string.Equals(endpointA, endpointB, StringComparison.Ordinal))
        {
            return;
        }
        if (string.CompareOrdinal(endpointA, endpointB) > 0)
        {
            (endpointA, endpointB) = (endpointB, endpointA);
        }
        _topology.Add($"link|{Field(endpointA)}|{Field(endpointB)}");
    }

    internal void AddOpcUaAttribute(string plcName, string name, string value) =>
        _opcUa.Add($"opcua-attr|{Field(plcName)}|{Field(name)}={Field(value)}");

    internal void AddOpcUaServerInterface(string plcName, string interfaceName, bool enabled, string? contentHash) =>
        _opcUa.Add($"opcua-server-interface|{Field(plcName)}|{Field(interfaceName)}|enabled={(enabled ? "true" : "false")}|hash={Field(contentHash)}");

    /// <summary>Keeps the line structure intact when names/values contain separators or newlines.</summary>
    private static string Field(string? value) =>
        (value ?? "-").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');

    // --- Openness capture (not unit-testable without TIA) ---

    private void CaptureSubnets(Project project, ILogger logger)
    {
        foreach (Subnet subnet in project.Subnets)
        {
            try
            {
                AddSubnet(subnet.Name, subnet.NetType.ToString(), subnet.TypeIdentifier);
                foreach (Node node in subnet.Nodes)
                {
                    AddSubnetNode(subnet.Name, node.Name);
                }
                foreach (IoSystem ioSystem in subnet.IoSystems)
                {
                    AddSubnetIoSystem(subnet.Name, ioSystem.Name, ioSystem.Number);
                }
                CaptureMrpDomains(subnet, logger);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "network fingerprint: skipping subnet {Subnet}", subnet.Name);
            }
        }
    }

    private void CaptureMrpDomains(Subnet subnet, ILogger logger)
    {
        MrpDomainOwner? owner;
        try
        {
            owner = subnet.GetService<MrpDomainOwner>();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "network fingerprint: MRP domains not readable for subnet {Subnet}", subnet.Name);
            return;
        }
        if (owner is null)
        {
            return;
        }
        foreach (MrpDomain domain in owner.MrpDomains)
        {
            try
            {
                AddMrpDomain(subnet.Name, domain.Name, ReadAttributes(domain).ToList());
                foreach (NetworkInterface participant in domain.DomainParticipants)
                {
                    AddMrpParticipant(subnet.Name, domain.Name, InterfaceIdentity(participant));
                }
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "network fingerprint: skipping MRP domain {Domain}", domain.Name);
            }
        }
    }

    private void CaptureDeviceItems(string deviceName, DeviceItemComposition items, ILogger logger)
    {
        foreach (DeviceItem item in items)
        {
            try
            {
                CaptureInterface(item, deviceName, logger);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "network fingerprint: skipping item {Item} of {Device}", item.Name, deviceName);
            }
            CaptureDeviceItems(deviceName, item.DeviceItems, logger);
        }
    }

    private void CaptureInterface(DeviceItem item, string deviceName, ILogger logger)
    {
        var networkInterface = item.GetService<NetworkInterface>();
        if (networkInterface is null)
        {
            return;
        }

        var itemPath = ItemPath(item);
        AddInterface(
            deviceName,
            itemPath,
            networkInterface.InterfaceType.ToString(),
            networkInterface.InterfaceOperatingMode.ToString());
        foreach (var attribute in ReadAttributes(networkInterface))
        {
            AddInterfaceAttribute(deviceName, itemPath, attribute.Key, attribute.Value);
        }

        foreach (Node node in networkInterface.Nodes)
        {
            AddNode(
                deviceName,
                itemPath,
                node.Name,
                node.NodeType.ToString(),
                node.NodeId,
                node.ConnectedSubnet?.Name);
            foreach (var attribute in ReadAttributes(node))
            {
                AddNodeAttribute(deviceName, itemPath, node.Name, attribute.Key, attribute.Value);
            }
        }

        foreach (NetworkPort port in networkInterface.Ports)
        {
            var endpoint = PortIdentity(port);
            foreach (NetworkPort partner in port.ConnectedPorts)
            {
                AddTopologyLink(endpoint, PortIdentity(partner));
            }
        }

        foreach (IoController controller in networkInterface.IoControllers)
        {
            AddIoController(deviceName, itemPath, controller.IoSystem?.Name, controller.IoSystem?.Number);
        }
        foreach (IoConnector connector in networkInterface.IoConnectors)
        {
            AddIoConnector(deviceName, itemPath, connector.ConnectedToIoSystem?.Name);
        }
    }

    private void CaptureOpcUa(PlcSoftware plc, ILogger logger)
    {
        var provider = plc.GetService<OpcUaProvider>();
        if (provider is null)
        {
            return;
        }
        foreach (var attribute in ReadAttributes(provider))
        {
            AddOpcUaAttribute(plc.Name, attribute.Key, attribute.Value);
        }
        var group = provider.CommunicationGroup?.ServerInterfaceGroup;
        if (group is null)
        {
            return;
        }
        foreach (ServerInterface serverInterface in group.ServerInterfaces)
        {
            AddOpcUaServerInterface(plc.Name, serverInterface.Name, serverInterface.Enabled, ExportHash(serverInterface, logger));
        }
        foreach (SimaticInterface simaticInterface in group.SimaticInterfaces)
        {
            AddOpcUaServerInterface(plc.Name, simaticInterface.Name, simaticInterface.Enabled, ExportHash(simaticInterface, logger));
        }
    }

    /// <summary>Exports one OPC UA interface definition to a temp file and hashes the XML;
    /// null when the export is unavailable.</summary>
    private static string? ExportHash(ServerInterface serverInterface, ILogger logger) =>
        ExportHash(serverInterface.Name, serverInterface.Export, logger);

    private static string? ExportHash(SimaticInterface simaticInterface, ILogger logger) =>
        ExportHash(simaticInterface.Name, simaticInterface.Export, logger);

    private static string? ExportHash(string name, Action<FileInfo> export, ILogger logger)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "opcua-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            export(new FileInfo(tempPath));
            return XmlContentHash.TryComputeFile(tempPath);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "network fingerprint: OPC UA interface {Interface} export failed", name);
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Temp cleanup is best-effort.
            }
        }
    }

    /// <summary>Reads every readable attribute as a sorted name=value list. Attributes that throw
    /// for the current object type are skipped; non-scalar and timestamp values are dropped
    /// (object references are identity, and timestamps are runtime state that would make the
    /// fingerprint flap without a configuration change).</summary>
    private static IEnumerable<KeyValuePair<string, string>> ReadAttributes(IEngineeringObject target)
    {
        IList<EngineeringAttributeInfo> infos;
        try
        {
            infos = target.GetAttributeInfos();
        }
        catch
        {
            yield break;
        }

        var names = new List<string>();
        foreach (var info in infos)
        {
            if (info.AccessMode is not (EngineeringAttributeAccessMode.Read or EngineeringAttributeAccessMode.ReadWrite))
            {
                continue;
            }
            names.Add(info.Name);
        }
        names.Sort(StringComparer.Ordinal);

        foreach (var name in names)
        {
            object? value;
            try
            {
                value = target.GetAttribute(name);
            }
            catch
            {
                continue;
            }
            var text = FormatValue(value);
            if (text is not null)
            {
                yield return new KeyValuePair<string, string>(name, text);
            }
        }
    }

    internal static string? FormatValue(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        Enum enumeration => enumeration.ToString(),
        DateTime => null,
        IEngineeringObject => null,
        System.Collections.IEnumerable => null,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>Name path from the device down to this item, e.g. "PLC_1/PROFINET interface_1".</summary>
    private static string ItemPath(DeviceItem item)
    {
        var segments = new List<string>();
        HardwareObject? current = item;
        while (current is not null and not Device)
        {
            segments.Insert(0, current.Name);
            current = current.Parent as HardwareObject;
        }
        return string.Join("/", segments);
    }

    private static string PortIdentity(NetworkPort port) => InterfaceIdentity(port.Interface) + "/" + port.OwnedBy.Name;

    private static string InterfaceIdentity(NetworkInterface networkInterface)
    {
        var item = networkInterface.OwnedBy;
        HardwareObject? current = item;
        while (current is not null and not Device)
        {
            current = current.Parent as HardwareObject;
        }
        var deviceName = (current as Device)?.Name ?? "?";
        return deviceName + "/" + ItemPath(item);
    }
}
