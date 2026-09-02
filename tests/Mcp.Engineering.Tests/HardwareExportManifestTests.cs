using System.Text.Json;
using Mcp.Engineering.Export;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class HardwareExportManifestTests
{
    [Fact]
    public void ManifestRoundTripsProjectAndDeviceArtifacts()
    {
        var manifest = new HardwareExportManifest
        {
            ProjectAmlFile = "project.aml",
            ProjectLogFile = "project-export.log",
            ProjectSuccess = true,
            ProjectContentHash = "project-hash",
            Devices =
            {
                new HardwareExportManifestDevice
                {
                    DeviceName = "PLC_1",
                    TypeIdentifier = "OrderNumber:CPU",
                    AmlFile = "Devices/PLC_1/device.aml",
                    LogFile = "Devices/PLC_1/export.log",
                    Success = true,
                    ContentHash = "hash",
                },
            },
        };

        var json = HardwareExportManifestJsonSerializer.Serialize(manifest);
        var roundTripped = HardwareExportManifestJsonSerializer.Deserialize(json);

        Assert.Equal("project.aml", roundTripped.ProjectAmlFile);
        Assert.Equal("project-export.log", roundTripped.ProjectLogFile);
        Assert.True(roundTripped.ProjectSuccess);
        Assert.Equal("project-hash", roundTripped.ProjectContentHash);
        var device = Assert.Single(roundTripped.Devices);
        Assert.Equal("PLC_1", device.DeviceName);
        Assert.Equal("Devices/PLC_1/device.aml", device.AmlFile);
        Assert.True(device.Success);
    }

    [Fact]
    public void ManifestSerializerProducesIndentedJson()
    {
        var json = HardwareExportManifestJsonSerializer.Serialize(new HardwareExportManifest());

        Assert.Contains("\n", json);
        Assert.Contains("\"schemaVersion\"", json);
    }

    [Fact]
    public void ManifestRoundTripsNetworkConfigurationFields()
    {
        var manifest = new HardwareExportManifest
        {
            NetworkConfigurationFile = "network-configuration.txt",
            NetworkConfigurationHash = "network-hash",
            NetworkConfigurationError = "capture failed",
        };

        var roundTripped = HardwareExportManifestJsonSerializer.Deserialize(
            HardwareExportManifestJsonSerializer.Serialize(manifest));

        Assert.Equal("network-configuration.txt", roundTripped.NetworkConfigurationFile);
        Assert.Equal("network-hash", roundTripped.NetworkConfigurationHash);
        Assert.Equal("capture failed", roundTripped.NetworkConfigurationError);
    }

    [Fact]
    public void ManifestWrittenBeforeNetworkFingerprintDeserializesWithNullFields()
    {
        var roundTripped = HardwareExportManifestJsonSerializer.Deserialize("""
            { "projectAmlFile": "project.aml", "projectSuccess": true }
            """);

        Assert.Null(roundTripped.NetworkConfigurationFile);
        Assert.Null(roundTripped.NetworkConfigurationHash);
        Assert.Null(roundTripped.NetworkConfigurationError);
    }
}
