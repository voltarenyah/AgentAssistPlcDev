using Contracts.Engineering;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Agent.Workbench;

internal sealed record HardwareConfigurationSnapshot(
    IReadOnlyDictionary<string, string?> Artifacts)
{
    public static HardwareConfigurationSnapshot? Read(string root)
    {
        var manifestPath = HardwareConfigurationExport.ResolveArtifactPath(root, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var json = document.RootElement;
            var projectHash = OptionalString(json, "projectContentHash");
            var projectAmlPath = HardwareConfigurationExport.ResolveArtifactPath(root, "project.aml");
            // Recompute from the artifact so manifests written before the export-normalization
            // fix do not keep a timestamp-sensitive project hash alive forever.
            if (HardwareConfigurationExport.IsUsableProjectAml(projectAmlPath))
            {
                projectHash = XmlContentHash.TryComputeFile(projectAmlPath) ?? projectHash;
            }

            var artifacts = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["project"] = projectHash,
            };
            if (json.TryGetProperty("devices", out var devices)
                && devices.ValueKind == JsonValueKind.Array)
            {
                foreach (var device in devices.EnumerateArray())
                {
                    var name = OptionalString(device, "deviceName");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        artifacts[DeviceKey(name)] = OptionalString(device, "contentHash");
                    }
                }
            }

            return new HardwareConfigurationSnapshot(artifacts);
        }
        catch (Exception exception) when (
            exception is JsonException
            or IOException
            or UnauthorizedAccessException)
        {
            throw new WorkbenchLifecycleException(
                "HARDWARE_MANIFEST_INVALID",
                $"The saved hardware manifest could not be read: {exception.Message}");
        }
    }

    public static HardwareConfigurationSnapshot FromResults(
        IEnumerable<HardwareExportResult> results,
        string? exportRoot = null)
    {
        var exported = results.ToArray();
        var artifacts = exported
            .Where(result => result.Success)
            .ToDictionary(
                result => result.Scope == "project"
                    ? "project"
                    : DeviceKey(result.DeviceName ?? "(unnamed device)"),
                result => result.ContentHash,
                StringComparer.Ordinal);
        if (exportRoot is not null
            && exported.Any(result => result.Scope == "project")
            && (!artifacts.TryGetValue("project", out var projectHash)
                || string.IsNullOrWhiteSpace(projectHash)))
        {
            var projectAmlPath = HardwareConfigurationExport.ResolveArtifactPath(exportRoot, "project.aml");
            if (HardwareConfigurationExport.IsUsableProjectAml(projectAmlPath))
            {
                artifacts["project"] = XmlContentHash.TryComputeFile(projectAmlPath);
            }
        }

        return new HardwareConfigurationSnapshot(artifacts);
    }

    public static IReadOnlyList<HardwareConfigurationCompareArtifact> Compare(
        HardwareConfigurationSnapshot? local,
        HardwareConfigurationSnapshot live)
    {
        var localArtifacts = local?.Artifacts
            ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        var keys = localArtifacts.Keys
            .Concat(live.Artifacts.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        return keys.Select(key =>
        {
            var localExists = localArtifacts.ContainsKey(key);
            var liveExists = live.Artifacts.ContainsKey(key);
            var state = !localExists
                ? "new"
                : !liveExists
                    ? "missing"
                    : localArtifacts[key] is null || live.Artifacts[key] is null
                        ? "unknown"
                        : string.Equals(localArtifacts[key], live.Artifacts[key], StringComparison.Ordinal)
                            ? "same"
                            : "changed";
            var isDevice = key.StartsWith("device:", StringComparison.Ordinal);
            return new HardwareConfigurationCompareArtifact(
                isDevice ? "device" : "project",
                isDevice ? key["device:".Length..] : null,
                state);
        }).ToArray();
    }

    public static string DeviceKey(string name) => "device:" + name;

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

internal static class HardwareConfigurationExport
{
    public static IReadOnlyList<string> EnsureSucceeded(
        HardwareExportResult[] results,
        string outputRoot)
    {
        var failures = results.Where(result => !result.Success).ToArray();
        var projectAmlPath = ResolveArtifactPath(outputRoot, "project.aml");
        if (!IsUsableProjectAml(projectAmlPath))
        {
            throw new WorkbenchLifecycleException(
                "HARDWARE_EXPORT_INCOMPLETE",
                "Hardware configuration export failed: "
                + string.Join("; ", failures.Select(result =>
                    $"{result.Scope}{(result.DeviceName is null ? string.Empty : $" '{result.DeviceName}'")}: {result.Error}")));
        }

        if (!results.Any(result => result.Scope == "project"))
        {
            throw new WorkbenchLifecycleException(
                "HARDWARE_EXPORT_INCOMPLETE",
                "Hardware configuration export did not produce a project-level AML artifact.");
        }

        return failures.Select(result =>
            $"{result.Scope}{(result.DeviceName is null ? string.Empty : $" '{result.DeviceName}'")}: {result.Error}")
            .ToArray();
    }

    public static bool IsUsableProjectAml(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return false;
        }

        try
        {
            XDocument.Load(path, LoadOptions.PreserveWhitespace);
            return true;
        }
        catch (Exception exception) when (
            exception is XmlException
            or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string ResolveArtifactPath(string hardwareRoot, string fileName)
    {
        var canonicalPath = Path.Combine(hardwareRoot, fileName);
        if (File.Exists(canonicalPath))
        {
            return canonicalPath;
        }

        var legacyPath = Path.Combine(hardwareRoot, "Hardware", fileName);
        return File.Exists(legacyPath) ? legacyPath : canonicalPath;
    }
}
