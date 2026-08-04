using Agent.Workbench;
using System.Text.Json;
using Xunit;

namespace Agent.Tests;

public sealed class HardwareListReaderTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"hardware-list-reader-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ReadBomListsTypedComponentsWithPositionTypeAndVersion()
    {
        WriteHardwareAml("""
            <CAEXFile xmlns="http://www.automationml.org/StandardVersion2.1">
              <InstanceHierarchy Name="Project hierarchy">
                <InternalElement Name="Project" ID="project-1">
                  <InternalElement Name="PN/IE_1" ID="subnet-1">
                    <Attribute Name="Type"><Value>Ethernet</Value></Attribute>
                    <ExternalInterface ID="ei-subnet-1" Name="LogicalEndPoint_Subnet" RefBaseClassPath="CommunicationInterfaceClassLib/LogicalEndPoint" />
                    <SupportedRoleClass RefRoleClassPath="AutomationProjectConfigurationRoleClassLib/Subnet" />
                  </InternalElement>
                  <InternalElement Name="COF-BASE-A_4" ID="device-1">
                    <Attribute Name="TypeIdentifier"><Value>System:Device.ET200SP</Value></Attribute>
                    <InternalElement Name="Châssis_0" ID="rack-1">
                      <Attribute Name="TypeName"><Value>Rack</Value></Attribute>
                      <Attribute Name="PositionNumber"><Value>0</Value></Attribute>
                      <Attribute Name="TypeIdentifier"><Value>System:Rack.ET200SP</Value></Attribute>
                      <InternalElement Name="COF-LID-A" ID="head-1">
                        <Attribute Name="TypeName"><Value>IM 155-6 PN ST</Value></Attribute>
                        <Attribute Name="PositionNumber"><Value>0</Value></Attribute>
                        <Attribute Name="TypeIdentifier"><Value>OrderNumber:6ES7 155-6AU01-0BN0</Value></Attribute>
                        <Attribute Name="FirmwareVersion"><Value>V4.2</Value></Attribute>
                        <InternalElement Name="1200MOD2" ID="module-1">
                          <Attribute Name="TypeName"><Value>AI 4xRTD/TC 2-,3-,4-wire HF</Value></Attribute>
                          <Attribute Name="PositionNumber"><Value>1</Value></Attribute>
                          <Attribute Name="TypeIdentifier"><Value>OrderNumber:6ES7 134-6JD00-0CA1</Value></Attribute>
                          <Attribute Name="FirmwareVersion"><Value>V2.0</Value></Attribute>
                        </InternalElement>
                      </InternalElement>
                    </InternalElement>
                  </InternalElement>
                </InternalElement>
              </InstanceHierarchy>
            </CAEXFile>
            """);

        var view = HardwareListReader.ReadBom(root);

        Assert.Equal("available", view.State);
        Assert.Equal("2026-08-04T00:00:00Z", view.ExportedAt);
        Assert.Equal(4, view.Items.Count);

        var device = Assert.Single(view.Items, item => item.Name == "COF-BASE-A_4");
        Assert.Equal("System:Device.ET200SP", device.TypeIdentifier);
        Assert.Null(device.OrderNumber);
        Assert.Equal("Project", device.Position);

        var head = Assert.Single(view.Items, item => item.Name == "COF-LID-A");
        Assert.Equal("OrderNumber:6ES7 155-6AU01-0BN0", head.TypeIdentifier);
        Assert.Equal("6ES7 155-6AU01-0BN0", head.OrderNumber);
        Assert.Equal("IM 155-6 PN ST", head.TypeName);
        Assert.Equal("V4.2", head.FirmwareVersion);
        Assert.Equal(0, head.PositionNumber);
        Assert.Equal("Project / COF-BASE-A_4 / Châssis_0", head.Position);
        Assert.Equal("Project.COF-BASE-A_4.Châssis_0.COF-LID-A", head.Path);

        var module = Assert.Single(view.Items, item => item.Name == "1200MOD2");
        Assert.Equal(1, module.PositionNumber);
        Assert.Equal("6ES7 134-6JD00-0CA1", module.OrderNumber);

        // Untyped subnets are not components.
        Assert.DoesNotContain(view.Items, item => item.Name == "PN/IE_1");
    }

    [Fact]
    public void ReadNetworkListsAddressedNodesWithDeviceAndSubnetNames()
    {
        WriteHardwareAml("""
            <CAEXFile xmlns="http://www.automationml.org/StandardVersion2.1">
              <InstanceHierarchy Name="Project hierarchy">
                <InternalElement Name="Project" ID="project-1">
                  <InternalElement Name="PN/IE_1" ID="subnet-1">
                    <Attribute Name="Type"><Value>Ethernet</Value></Attribute>
                    <ExternalInterface ID="ei-subnet-1" Name="LogicalEndPoint_Subnet" RefBaseClassPath="CommunicationInterfaceClassLib/LogicalEndPoint" />
                    <SupportedRoleClass RefRoleClassPath="AutomationProjectConfigurationRoleClassLib/Subnet" />
                  </InternalElement>
                  <InternalElement Name="COF-BASE-A_4" ID="device-1">
                    <Attribute Name="TypeIdentifier"><Value>System:Device.ET200SP</Value></Attribute>
                    <InternalElement Name="COF-LID-A" ID="head-1">
                      <Attribute Name="TypeIdentifier"><Value>OrderNumber:6ES7 155-6AU01-0BN0</Value></Attribute>
                      <InternalElement Name="Interface PROFINET" ID="iface-1">
                        <Attribute Name="Label"><Value>X1</Value></Attribute>
                        <InternalElement Name="IE1" ID="node-1">
                          <Attribute Name="SubnetMask"><Value>255.255.255.0</Value></Attribute>
                          <Attribute Name="ProfinetDeviceName"><Value>cof-lid-a</Value></Attribute>
                          <Attribute Name="NetworkAddress"><Value>192.168.1.11</Value></Attribute>
                          <ExternalInterface ID="ei-node-1" Name="LogicalEndPoint_Node" RefBaseClassPath="CommunicationInterfaceClassLib/LogicalEndPoint" />
                          <SupportedRoleClass RefRoleClassPath="AutomationProjectConfigurationRoleClassLib/Node" />
                        </InternalElement>
                      </InternalElement>
                    </InternalElement>
                    <InternalElement Name="Unlinked interface" ID="iface-2">
                      <InternalElement Name="IE1" ID="node-2">
                        <Attribute Name="NetworkAddress"><Value>10.0.0.5</Value></Attribute>
                        <ExternalInterface ID="ei-node-2" Name="LogicalEndPoint_Node" RefBaseClassPath="CommunicationInterfaceClassLib/LogicalEndPoint" />
                      </InternalElement>
                    </InternalElement>
                  </InternalElement>
                  <InternalLink Name="Link To Subnet_1" RefPartnerSideA="node-1:LogicalEndPoint_Node" RefPartnerSideB="subnet-1:LogicalEndPoint_Subnet" />
                </InternalElement>
              </InstanceHierarchy>
            </CAEXFile>
            """);

        var view = HardwareListReader.ReadNetwork(root);

        Assert.Equal("available", view.State);
        Assert.Equal(2, view.Nodes.Count);

        var linked = Assert.Single(view.Nodes, node => node.Address == "192.168.1.11");
        Assert.Equal("COF-BASE-A_4", linked.DeviceName);
        Assert.Equal("Project.COF-BASE-A_4", linked.DevicePath);
        Assert.Equal("PN/IE_1", linked.SubnetName);
        Assert.Equal("255.255.255.0", linked.SubnetMask);
        Assert.Equal("cof-lid-a", linked.ProfinetDeviceName);
        Assert.Equal("X1", linked.InterfaceLabel);

        var unlinked = Assert.Single(view.Nodes, node => node.Address == "10.0.0.5");
        Assert.Null(unlinked.SubnetName);
        Assert.Null(unlinked.SubnetMask);
        Assert.Null(unlinked.ProfinetDeviceName);
        Assert.Null(unlinked.InterfaceLabel);
    }

    [Fact]
    public void ReadersReportMissingStateWhenNoHardwareExportExists()
    {
        Directory.CreateDirectory(root);

        var bom = HardwareListReader.ReadBom(root);
        var network = HardwareListReader.ReadNetwork(root);

        Assert.Equal("missing", bom.State);
        Assert.Empty(bom.Items);
        Assert.NotNull(bom.Message);
        Assert.Equal("missing", network.State);
        Assert.Empty(network.Nodes);
        Assert.NotNull(network.Message);
    }

    private void WriteHardwareAml(string aml)
    {
        var hardwareRoot = Path.Combine(root, "hardware");
        Directory.CreateDirectory(hardwareRoot);
        File.WriteAllText(
            Path.Combine(hardwareRoot, "manifest.json"),
            JsonSerializer.Serialize(new
            {
                projectAmlFile = "project.aml",
                exportedAt = "2026-08-04T00:00:00Z",
            }));
        File.WriteAllText(Path.Combine(hardwareRoot, "project.aml"), aml);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
