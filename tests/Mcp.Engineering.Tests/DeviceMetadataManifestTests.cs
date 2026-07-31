using System;
using System.Collections.Generic;
using System.IO;
using Mcp.Engineering.Export;
using Xunit;

namespace Mcp.Engineering.Tests;

/// <summary>
/// Round-trip and merge behavior of the manifest's additive "device" section
/// (<see cref="DeviceMetadata"/>, 2026-07-31): serializer round-trip, legacy-manifest tolerance,
/// and the WriteAll/Upsert preservation rule (a writer that has no fresh capture keeps the
/// previously stored section instead of dropping it).
/// </summary>
public sealed class DeviceMetadataManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "device-metadata-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SerializeDeserialize_RoundTripsDeviceSection()
    {
        var document = new ExportMetadataDocument
        {
            ExportRoot = "/exports/PLC_1",
            Device = Sample(),
        };

        var roundTripped = ExportMetadataJsonSerializer.Deserialize(
            ExportMetadataJsonSerializer.Serialize(document));

        Assert.NotNull(roundTripped.Device);
        var device = roundTripped.Device!;
        Assert.Equal("PLC_1", device.PlcName);
        Assert.Equal("Station_1", device.DeviceName);
        Assert.Equal("OrderNumber:6ES7515-2AM02-0AB0/V2.9", device.TypeIdentifier);
        Assert.Equal("TestPLCExportDemo", device.ProjectName);
        Assert.Equal("Ansel", device.ProjectAuthor);
        Assert.Equal("demo project", device.ProjectComment);
        Assert.Equal("V17", device.ProjectVersion);
        Assert.Equal("ACME", device.ProjectCopyright);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero), device.ProjectCreationTime);
        Assert.Equal(new DateTimeOffset(2026, 7, 30, 9, 30, 0, TimeSpan.Zero), device.ProjectLastModified);
        Assert.Equal("Ansel", device.ProjectLastModifiedBy);
    }

    [Fact]
    public void Serialize_WritesNullDevice_AsExplicitNull()
    {
        var json = ExportMetadataJsonSerializer.Serialize(new ExportMetadataDocument());

        Assert.Contains("\"device\": null", json);
        var roundTripped = ExportMetadataJsonSerializer.Deserialize(json);
        Assert.Null(roundTripped.Device);
    }

    [Fact]
    public void Deserialize_LegacyManifestWithoutDevice_Tolerates()
    {
        const string legacy = """
            {
              "schemaVersion": "1.0",
              "exportStartedUtc": "2026-07-18T06:00:00.0000000+00:00",
              "exportFinishedUtc": "2026-07-18T06:01:00.0000000+00:00",
              "exportRoot": "/exports/PLC_1",
              "components": []
            }
            """;

        var document = ExportMetadataJsonSerializer.Deserialize(legacy);

        Assert.Null(document.Device);
        Assert.Empty(document.Components);
    }

    [Fact]
    public void WriteAll_StoresDevice_AndPreservesItWhenLaterWriteHasNoCapture()
    {
        Directory.CreateDirectory(_root);
        var record = new ExportMetadataRecord { Id = "id1", Name = "A", Category = "FB", Status = "Exported" };

        ExportManifest.WriteAll(_root, DateTimeOffset.UtcNow, new List<ExportMetadataRecord> { record },
            ExportManifest.BlockCategories, Sample());
        Assert.Equal("PLC_1", Read().Device?.PlcName);

        // A later write without a fresh capture must not drop the stored section.
        ExportManifest.WriteAll(_root, DateTimeOffset.UtcNow, new List<ExportMetadataRecord> { record },
            ExportManifest.BlockCategories);
        Assert.Equal("PLC_1", Read().Device?.PlcName);
        Assert.Equal("TestPLCExportDemo", Read().Device?.ProjectName);
    }

    [Fact]
    public void Upsert_PreservesExistingDevice_WhenNoCaptureProvided()
    {
        Directory.CreateDirectory(_root);
        var record = new ExportMetadataRecord { Id = "id1", Name = "A", Category = "FB", Status = "Exported" };
        ExportManifest.WriteAll(_root, DateTimeOffset.UtcNow, new List<ExportMetadataRecord> { record },
            ExportManifest.BlockCategories, Sample());

        ExportManifest.Upsert(_root, record);

        Assert.Equal("PLC_1", Read().Device?.PlcName);
    }

    [Fact]
    public void Upsert_WithFreshCapture_ReplacesStoredDevice()
    {
        Directory.CreateDirectory(_root);
        var record = new ExportMetadataRecord { Id = "id1", Name = "A", Category = "FB", Status = "Exported" };
        ExportManifest.WriteAll(_root, DateTimeOffset.UtcNow, new List<ExportMetadataRecord> { record },
            ExportManifest.BlockCategories, Sample());

        ExportManifest.Upsert(_root, record, new DeviceMetadata { PlcName = "PLC_2" });

        Assert.Equal("PLC_2", Read().Device?.PlcName);
    }

    private ExportMetadataDocument Read() =>
        ExportMetadataJsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(_root, ExportManifest.MetadataFileName)));

    private static DeviceMetadata Sample() => new()
    {
        PlcName = "PLC_1",
        DeviceName = "Station_1",
        TypeIdentifier = "OrderNumber:6ES7515-2AM02-0AB0/V2.9",
        ProjectName = "TestPLCExportDemo",
        ProjectAuthor = "Ansel",
        ProjectComment = "demo project",
        ProjectVersion = "V17",
        ProjectCopyright = "ACME",
        ProjectCreationTime = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
        ProjectLastModified = new DateTimeOffset(2026, 7, 30, 9, 30, 0, TimeSpan.Zero),
        ProjectLastModifiedBy = "Ansel",
    };
}
