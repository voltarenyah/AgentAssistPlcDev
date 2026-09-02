using System;
using Mcp.Engineering.Export;
using Xunit;

namespace Mcp.Engineering.Tests;

/// <summary>
/// The fingerprint text must be byte-identical for identical network configurations regardless
/// of Openness enumeration order — the compare verdict depends on it (issue #69).
/// </summary>
public sealed class NetworkConfigurationFingerprintTests
{
    [Fact]
    public void SerializeIsDeterministicRegardlessOfInsertionOrder()
    {
        var first = BuildFingerprint();
        var second = new NetworkConfigurationFingerprint();
        second.AddTopologyLink("PLC_2/PN_1/Port_1", "PLC_1/PN_1/Port_2");
        second.AddInterfaceAttribute("PLC_1", "PN_1", "ProfinetDeviceName", "plc-1");
        second.AddInterface("PLC_1", "PN_1", "Ethernet", "IoController");
        second.AddSubnet("PN/IE_1", "Ethernet", "-");
        second.AddOpcUaServerInterface("PLC_1", "OPC_UA_1", true, "hash");

        Assert.Equal(first.Serialize(), second.Serialize());
    }

    [Fact]
    public void SerializeUsesFixedSectionLayout()
    {
        var text = new NetworkConfigurationFingerprint().Serialize();

        Assert.Equal(
            "network-configuration-fingerprint/v1\n[subnets]\n[interfaces]\n[io-assignments]\n[topology]\n[opcua]\n",
            text);
    }

    [Fact]
    public void TopologyLinkEndpointsAreNormalizedDeduplicatedAndSelfLinksDropped()
    {
        var fingerprint = new NetworkConfigurationFingerprint();
        fingerprint.AddTopologyLink("b", "a");
        fingerprint.AddTopologyLink("a", "b");
        fingerprint.AddTopologyLink("a", "a");

        Assert.Equal(
            "network-configuration-fingerprint/v1\n[subnets]\n[interfaces]\n[io-assignments]\n[topology]\nlink|a|b\n[opcua]\n",
            fingerprint.Serialize());
    }

    [Fact]
    public void FieldSeparatorsAndNewlinesInValuesCannotBreakTheLineStructure()
    {
        var fingerprint = new NetworkConfigurationFingerprint();
        fingerprint.AddInterfaceAttribute("PLC_1", "PN_1", "Comment", "a|b\nc");

        var lines = fingerprint.Serialize().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("interface-attr|PLC_1|PN_1|Comment=a/b c", lines);
        Assert.Equal(7, lines.Length);
    }

    private static NetworkConfigurationFingerprint BuildFingerprint()
    {
        var fingerprint = new NetworkConfigurationFingerprint();
        fingerprint.AddSubnet("PN/IE_1", "Ethernet", "-");
        fingerprint.AddInterface("PLC_1", "PN_1", "Ethernet", "IoController");
        fingerprint.AddInterfaceAttribute("PLC_1", "PN_1", "ProfinetDeviceName", "plc-1");
        fingerprint.AddTopologyLink("PLC_1/PN_1/Port_2", "PLC_2/PN_1/Port_1");
        fingerprint.AddOpcUaServerInterface("PLC_1", "OPC_UA_1", true, "hash");
        return fingerprint;
    }
}
