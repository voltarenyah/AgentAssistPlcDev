using System.Text.Json;
using System.Xml.Linq;

namespace Agent.Workbench;

public sealed record HardwareConfigurationView(
    string State,
    string? ProjectAmlPath,
    string? ExportedAt,
    IReadOnlyList<HardwareConfigurationNode> Devices,
    IReadOnlyList<HardwareConfigurationTag> Tags,
    string? Message);

public sealed record HardwareConfigurationNode(
    string Id,
    string Name,
    string Path,
    string Kind,
    string? TypeIdentifier,
    IReadOnlyList<HardwareConfigurationProperty> Properties,
    IReadOnlyList<HardwareConfigurationIoRange> IoRanges,
    IReadOnlyList<HardwareConfigurationNode> Children);

public sealed record HardwareConfigurationProperty(string Name, string Value);

public sealed record HardwareConfigurationIoRange(
    string IoType,
    int StartAddress,
    int LengthBits,
    int EndAddress,
    string AddressRange);

public sealed record HardwareConfigurationTag(
    string Id,
    string Name,
    string DataType,
    string IoType,
    string LogicalAddress,
    string? OwnerPath);

public static class HardwareConfigurationReader
{
    public static HardwareConfigurationView Read(string worktreeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeRoot);
        var source = HardwareAml.Resolve(worktreeRoot);
        if (source.State != "available" || source.Document is null)
        {
            return new HardwareConfigurationView(
                source.State,
                source.ProjectAmlPath,
                source.ExportedAt,
                Array.Empty<HardwareConfigurationNode>(),
                Array.Empty<HardwareConfigurationTag>(),
                source.Message);
        }

        try
        {
            var document = source.Document;
            var deviceElements = document
                .Descendants()
                .Where(element => Is(element, "InternalElement")
                    && TypeIdentifier(element) is not null
                    && !element.Ancestors().Any(parent =>
                        Is(parent, "InternalElement") && TypeIdentifier(parent) is not null))
                .ToArray();
            var devices = deviceElements
                .Select(element => ReadNode(element, "device", null))
                .ToArray();
            var tags = document
                .Descendants()
                .Where(element => Is(element, "ExternalInterface"))
                .Select(ReadTag)
                .Where(tag => tag is not null)
                .Cast<HardwareConfigurationTag>()
                .ToArray();
            return new HardwareConfigurationView(
                "available",
                source.ProjectAmlPath,
                source.ExportedAt,
                devices,
                tags,
                devices.Length == 0
                    ? "The project AML contains no top-level hardware objects."
                    : null);
        }
        catch (Exception exception) when (
            exception is JsonException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or System.Xml.XmlException)
        {
            return new HardwareConfigurationView(
                "invalid",
                null,
                null,
                Array.Empty<HardwareConfigurationNode>(),
                Array.Empty<HardwareConfigurationTag>(),
                $"The saved project AML could not be read: {exception.Message}");
        }
    }

    private static HardwareConfigurationNode ReadNode(XElement element, string kind, string? parentPath)
    {
        var name = AttributeValue(element, "Name") ?? "Unnamed hardware object";
        var path = string.IsNullOrWhiteSpace(parentPath) ? name : $"{parentPath}.{name}";
        var properties = ReadProperties(element).ToArray();
        var typeIdentifier = AttributeValue(element, "RefBaseSystemUnitPath")
            ?? properties.FirstOrDefault(property =>
                string.Equals(property.Name, "TypeIdentifier", StringComparison.OrdinalIgnoreCase))?.Value;
        var id = AttributeValue(element, "ID") ?? path;
        var ioRanges = ReadIoRanges(element).ToList();
        if (ioRanges.Count == 0)
        {
            foreach (var builtInChild in element
                .Elements()
                .Where(child => Is(child, "InternalElement"))
                .Where(child => string.Equals(AttributeValue(child, "Name"), name, StringComparison.OrdinalIgnoreCase)))
            {
                ioRanges.AddRange(ReadIoRanges(builtInChild));
            }
        }
        ioRanges = ioRanges
            .DistinctBy(range => (range.IoType, range.StartAddress, range.LengthBits))
            .ToList();
        var children = element
            .Elements()
            .Where(child => Is(child, "InternalElement"))
            .Select(child => ReadNode(child, "module", path))
            .ToArray();

        return new HardwareConfigurationNode(id, name, path, kind, typeIdentifier, properties, ioRanges, children);
    }

    private static IEnumerable<HardwareConfigurationProperty> ReadProperties(XElement element)
    {
        foreach (var attribute in element.Elements().Where(child => Is(child, "Attribute")))
        {
            foreach (var property in ReadProperty(attribute, null))
            {
                yield return property;
            }
        }
    }

    private static IEnumerable<HardwareConfigurationProperty> ReadProperty(XElement element, string? prefix)
    {
        var name = AttributeValue(element, "Name");
        if (string.IsNullOrWhiteSpace(name)) yield break;

        var propertyName = string.IsNullOrWhiteSpace(prefix) ? name : $"{prefix}.{name}";
        var nested = element.Elements().Where(child => Is(child, "Attribute")).ToArray();
        if (nested.Length > 0)
        {
            foreach (var child in nested)
            {
                foreach (var property in ReadProperty(child, propertyName))
                {
                    yield return property;
                }
            }

            yield break;
        }

        var value = element
            .Descendants()
            .FirstOrDefault(child => Is(child, "Value"))?
            .Value
            .Trim() ?? string.Empty;
        yield return new HardwareConfigurationProperty(propertyName, NormalizeValue(value));
    }

    private static IEnumerable<HardwareConfigurationIoRange> ReadIoRanges(XElement element)
    {
        var attributes = element
            .Descendants()
            .Where(child => Is(child, "Attribute"))
            .Where(attribute => attribute.Ancestors().FirstOrDefault(IsInternalElement) == element);

        foreach (var group in attributes)
        {
            var values = group
                .Elements()
                .Where(child => Is(child, "Attribute"))
                .Select(child => new
                {
                    Name = AttributeValue(child, "Name"),
                    Value = child.Descendants().FirstOrDefault(value => Is(value, "Value"))?.Value.Trim(),
                })
                .Where(value => !string.IsNullOrWhiteSpace(value.Name))
                .ToDictionary(value => value.Name!, value => value.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

            if (!values.TryGetValue("StartAddress", out var startText)
                || !values.TryGetValue("Length", out var lengthText)
                || !values.TryGetValue("IoType", out var ioType)
                || !int.TryParse(startText, out var startAddress)
                || !int.TryParse(lengthText, out var lengthBits)
                || lengthBits <= 0
                || string.IsNullOrWhiteSpace(ioType))
            {
                continue;
            }

            var byteCount = (lengthBits + 7) / 8;
            var endAddress = startAddress + byteCount - 1;
            var prefix = string.Equals(ioType, "Input", StringComparison.OrdinalIgnoreCase) ? "I"
                : string.Equals(ioType, "Output", StringComparison.OrdinalIgnoreCase) ? "Q"
                : ioType.Trim();
            var endBit = (lengthBits - 1) % 8;
            yield return new HardwareConfigurationIoRange(
                NormalizeValue(ioType),
                startAddress,
                lengthBits,
                endAddress,
                $"{prefix}{startAddress}.0 to {prefix}{endAddress}.{endBit}");
        }
    }

    private static HardwareConfigurationTag? ReadTag(XElement element)
    {
        var refBaseClassPath = AttributeValue(element, "RefBaseClassPath");
        if (string.IsNullOrWhiteSpace(refBaseClassPath)
            || !refBaseClassPath.Contains("Tag", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var values = element
            .Elements()
            .Where(child => Is(child, "Attribute"))
            .SelectMany(attribute => ReadProperty(attribute, null))
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("DataType", out var dataType)
            || !values.TryGetValue("IoType", out var ioType)
            || !values.TryGetValue("LogicalAddress", out var logicalAddress)
            || string.IsNullOrWhiteSpace(logicalAddress))
        {
            return null;
        }

        var ownerPath = string.Join(
            ".",
            element.Ancestors()
                .Where(IsInternalElement)
                .Reverse()
                .Select(owner => AttributeValue(owner, "Name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))!);
        return new HardwareConfigurationTag(
            AttributeValue(element, "ID") ?? AttributeValue(element, "Name") ?? logicalAddress,
            AttributeValue(element, "Name") ?? "Unnamed tag",
            NormalizeValue(dataType),
            NormalizeValue(ioType),
            NormalizeValue(logicalAddress),
            string.IsNullOrWhiteSpace(ownerPath) ? null : ownerPath);
    }

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? TypeIdentifier(XElement element) =>
        AttributeValue(element, "RefBaseSystemUnitPath")
        ?? element.Elements()
            .Where(child => Is(child, "Attribute"))
            .SelectMany(child => ReadProperty(child, null))
            .FirstOrDefault(property =>
                string.Equals(property.Name, "TypeIdentifier", StringComparison.OrdinalIgnoreCase))?.Value;

    private static string NormalizeValue(string value)
    {
        var normalized = value.Trim();
        return normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"'
            ? normalized[1..^1]
            : normalized;
    }

    private static bool IsInternalElement(XElement element) => Is(element, "InternalElement");

    private static bool Is(XElement element, string localName) =>
        string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);

}

/// <summary>
/// Locates and loads the exported hardware AML (<c>hardware/project.aml</c> referenced by
/// <c>manifest.json</c>) for a worktree. Shared by the hardware readers so they all agree on
/// the on-disk layout and the missing/invalid states.
/// </summary>
internal sealed record HardwareAmlSource(
    string State,
    string? ProjectAmlPath,
    string? ExportedAt,
    XDocument? Document,
    string? Message);

internal static class HardwareAml
{
    public static HardwareAmlSource Resolve(string worktreeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeRoot);
        var hardwareRoot = WorkbenchPaths.ResolveHardwareRoot(worktreeRoot);
        var legacyHardwareRoot = WorkbenchPaths.ResolveRelative(hardwareRoot, "Hardware");
        var manifestRoot = File.Exists(Path.Combine(hardwareRoot, "manifest.json"))
            || File.Exists(Path.Combine(hardwareRoot, "project.aml"))
            || !File.Exists(Path.Combine(legacyHardwareRoot, "manifest.json"))
                ? hardwareRoot
                : legacyHardwareRoot;
        var manifestPath = Path.Combine(manifestRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new HardwareAmlSource(
                "missing",
                null,
                null,
                null,
                "No saved project-level hardware configuration is available. Reload it from TIA.");
        }

        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var json = manifest.RootElement;
            var projectAmlRelative = OptionalString(json, "projectAmlFile") ?? "project.aml";
            var projectAmlPath = WorkbenchPaths.ResolveRelative(manifestRoot, projectAmlRelative);
            var exportedAt = OptionalString(json, "exportedAt");
            if (!File.Exists(projectAmlPath))
            {
                return new HardwareAmlSource(
                    "invalid",
                    projectAmlPath,
                    exportedAt,
                    null,
                    $"The saved hardware manifest references a missing AML file: {projectAmlRelative}.");
            }

            var document = XDocument.Load(projectAmlPath, LoadOptions.PreserveWhitespace);
            return new HardwareAmlSource("available", projectAmlPath, exportedAt, document, null);
        }
        catch (Exception exception) when (
            exception is JsonException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or System.Xml.XmlException)
        {
            return new HardwareAmlSource(
                "invalid",
                null,
                null,
                null,
                $"The saved project AML could not be read: {exception.Message}");
        }
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
