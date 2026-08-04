using Agent.Workbench;
using System.Text.Json;
using Xunit;

namespace Agent.Tests;

public sealed class HardwareConfigurationReaderTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"hardware-reader-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ReadBuildsDeviceAndChildModuleHierarchyFromProjectAml()
    {
        var hardwareRoot = Path.Combine(root, "hardware");
        Directory.CreateDirectory(hardwareRoot);
        File.WriteAllText(
            Path.Combine(hardwareRoot, "manifest.json"),
            JsonSerializer.Serialize(new
            {
                projectAmlFile = "project.aml",
                projectSuccess = true,
                exportedAt = "2026-08-04T00:00:00Z",
            }));
        File.WriteAllText(Path.Combine(hardwareRoot, "project.aml"), """
            <CAEXFile xmlns="http://www.automationml.org/StandardVersion2.1">
              <InstanceHierarchy Name="Project hierarchy">
                <InternalElement Name="Project" ID="project-1">
                  <InternalElement Name="PLC_1" ID="device-1">
                    <Attribute Name="TypeIdentifier"><Value>AutomationML/PLC</Value></Attribute>
                    <Attribute Name="Order number"><Value>6ES7-PLC</Value></Attribute>
                    <InternalElement Name="DI_1" ID="module-1">
                      <Attribute Name="TypeIdentifier"><Value>AutomationML/Module</Value></Attribute>
                      <Attribute Name="Slot"><Value>1</Value></Attribute>
                      <InternalElement Name="DI_1">
                        <Attribute Name="1">
                          <Attribute Name="StartAddress"><Value>460</Value></Attribute>
                          <Attribute Name="Length"><Value>56</Value></Attribute>
                          <Attribute Name="IoType"><Value>Input</Value></Attribute>
                        </Attribute>
                      </InternalElement>
                    </InternalElement>
                    <ExternalInterface ID="tag-1" Name="DI_1_Tag" RefBaseClassPath="AutomationProjectConfigurationInterfaceClassLib/Tag">
                      <Attribute Name="DataType"><Value>Bool</Value></Attribute>
                      <Attribute Name="IoType"><Value>Input</Value></Attribute>
                      <Attribute Name="LogicalAddress"><Value>460.3</Value></Attribute>
                    </ExternalInterface>
                  </InternalElement>
                </InternalElement>
              </InstanceHierarchy>
            </CAEXFile>
            """);

        var view = HardwareConfigurationReader.Read(root);

        var device = Assert.Single(view.Devices);
        var module = Assert.Single(device.Children);
        Assert.Equal("available", view.State);
        Assert.Equal("PLC_1", device.Name);
        Assert.Equal("device-1", device.Id);
        Assert.Equal("PLC_1", device.Path);
        Assert.Equal("AutomationML/PLC", device.TypeIdentifier);
        Assert.Equal("6ES7-PLC", Assert.Single(device.Properties, property => property.Name == "Order number").Value);
        Assert.Equal("DI_1", module.Name);
        Assert.Equal("PLC_1.DI_1", module.Path);
        Assert.Equal("1", Assert.Single(module.Properties, property => property.Name == "Slot").Value);
        var range = Assert.Single(module.IoRanges);
        Assert.Equal("Input", range.IoType);
        Assert.Equal(460, range.StartAddress);
        Assert.Equal(56, range.LengthBits);
        Assert.Equal(466, range.EndAddress);
        Assert.Equal("I460.0 to I466.7", range.AddressRange);
        Assert.Equal("460", Assert.Single(module.Children[0].Properties, property => property.Name == "1.StartAddress").Value);
        var tag = Assert.Single(view.Tags);
        Assert.Equal("DI_1_Tag", tag.Name);
        Assert.Equal("Bool", tag.DataType);
        Assert.Equal("Input", tag.IoType);
        Assert.Equal("460.3", tag.LogicalAddress);
        Assert.Equal("Project.PLC_1", tag.OwnerPath);
    }

    [Fact]
    public void ReadExtractsTopLevelTypedDevicesAndNestedModulesFromProjectHierarchy()
    {
        var hardwareRoot = Path.Combine(root, "hardware", "Hardware");
        Directory.CreateDirectory(hardwareRoot);
        File.WriteAllText(
            Path.Combine(hardwareRoot, "manifest.json"),
            JsonSerializer.Serialize(new { projectAmlFile = "project.aml" }));
        File.WriteAllText(Path.Combine(hardwareRoot, "project.aml"), """
            <CAEXFile xmlns="http://www.automationml.org/StandardVersion2.1">
              <InstanceHierarchy Name="Project hierarchy">
                <InternalElement Name="Project wrapper">
                  <InternalElement Name="PN/IE_1">
                    <Attribute Name="Type"><Value>Ethernet</Value></Attribute>
                  </InternalElement>
                  <InternalElement Name="PEI_SinoARP">
                    <InternalElement Name="SubModule">
                      <InternalElement Name="COF-BASE-A_1">
                        <Attribute Name="TypeIdentifier"><Value>System:Device.ET200SP</Value></Attribute>
                        <InternalElement Name="Châssis_0">
                          <InternalElement Name="AG">
                            <Attribute Name="TypeIdentifier"><Value>OrderNumber:HeadModule</Value></Attribute>
                            <InternalElement Name="200MOD2">
                              <Attribute Name="TypeIdentifier"><Value>OrderNumber:DigitalInput</Value></Attribute>
                            </InternalElement>
                            <InternalElement Name="200MOD17">
                              <Attribute Name="TypeIdentifier"><Value>OrderNumber:DigitalOutput</Value></Attribute>
                            </InternalElement>
                          </InternalElement>
                        </InternalElement>
                      </InternalElement>
                    </InternalElement>
                    <InternalElement Name="HMI_1">
                      <Attribute Name="TypeIdentifier"><Value>System:Device.HMI</Value></Attribute>
                    </InternalElement>
                  </InternalElement>
                </InternalElement>
              </InstanceHierarchy>
            </CAEXFile>
            """);

        var view = HardwareConfigurationReader.Read(root);

        Assert.Equal(new[] { "COF-BASE-A_1", "HMI_1" }, view.Devices.Select(device => device.Name));
        var et200 = Assert.Single(view.Devices, device => device.Name == "COF-BASE-A_1");
        var chassis = Assert.Single(et200.Children, child => child.Name == "Châssis_0");
        var ag = Assert.Single(chassis.Children, child => child.Name == "AG");
        Assert.Equal("COF-BASE-A_1.Châssis_0.AG", ag.Path);
        Assert.Equal(new[] { "200MOD2", "200MOD17" }, ag.Children.Select(module => module.Name));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
