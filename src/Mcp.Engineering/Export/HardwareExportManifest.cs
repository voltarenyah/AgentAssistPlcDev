using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcp.Engineering.Export;

/// <summary>Project-level index for the canonical project AML and optional device AML files.</summary>
public sealed class HardwareExportManifest
{
    public string SchemaVersion { get; set; } = "1.0";
    public string ProjectAmlFile { get; set; } = "project.aml";
    public string? ProjectLogFile { get; set; }
    public bool ProjectSuccess { get; set; }
    public string? ProjectError { get; set; }
    public string? ProjectContentHash { get; set; }
    /// <summary>Network/communication fingerprint artifact (network-configuration.txt), issue #69.</summary>
    public string? NetworkConfigurationFile { get; set; }
    public string? NetworkConfigurationHash { get; set; }
    /// <summary>Set when the network fingerprint capture failed; the AML export still succeeded.</summary>
    public string? NetworkConfigurationError { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
    public List<HardwareExportManifestDevice> Devices { get; set; } = new();
}

public sealed class HardwareExportManifestDevice
{
    public string DeviceName { get; set; } = string.Empty;
    public string? TypeIdentifier { get; set; }
    public string AmlFile { get; set; } = string.Empty;
    public string LogFile { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ContentHash { get; set; }
    public DateTimeOffset ExportedAt { get; set; }
}

internal static class HardwareExportManifestJsonSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(HardwareExportManifest manifest) =>
        JsonSerializer.Serialize(manifest, Options);

    public static HardwareExportManifest Deserialize(string json) =>
        JsonSerializer.Deserialize<HardwareExportManifest>(json, Options)
        ?? throw new InvalidDataException("Hardware export manifest is empty.");
}
