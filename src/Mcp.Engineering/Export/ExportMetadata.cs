using System.Text;
using System.Text.Json;
using Contracts.Engineering;

namespace Mcp.Engineering.Export;

// Provenance: ported from PlcSourceExporter.Core/ExportMetadata.cs (TIA Add-in project) so the
// mcp-knowledge server can consume both exporters' manifests with one schema. The JSON field
// naming/order and the stable-id formula are byte-for-byte compatible with the reference.
// Adaptations for this codebase: mutable DTOs (single-block export upserts re-read and rewrite
// the manifest), and a System.Text.Json-based Deserialize added for that upsert read path.
// Amendment 2026-07-31: additive top-level "device" object (DeviceMetadata) — not part of the
// reference schema, but additive-only and ignored by the tolerant readers (mcp-knowledge's
// System.Text.Json DTO import skips unknown properties), so the interop contract is preserved.

/// <summary>metadata.json document — schemaVersion "1.0" (locked 2026-07-18).</summary>
public sealed class ExportMetadataDocument
{
    public string SchemaVersion { get; set; } = "1.0";
    public DateTimeOffset ExportStartedUtc { get; set; }
    public DateTimeOffset ExportFinishedUtc { get; set; }
    public string ExportRoot { get; set; } = string.Empty;

    /// <summary>Device-level identity + project-level metadata captured from TIA Openness at
    /// export time (additive 2026-07-31 — consumers tolerate the extra field; see
    /// <see cref="DeviceMetadata"/>). Null for manifests written before this field existed.</summary>
    public DeviceMetadata? Device { get; set; }

    public List<ExportMetadataRecord> Components { get; set; } = new();
}

/// <summary>Project- and device-level metadata TIA Openness exposes (properties verified in
/// buildnote/bestpractice/openness-v17-api-surface.md §1/§2), stored per device so a UI page can
/// display it without a live TIA session. Project-level values repeat across devices of the same
/// project by design — the per-device metadata.json is the single read source for one device.</summary>
public sealed class DeviceMetadata
{
    /// <summary>PLC software name (PlcSoftware.Name) — the device's folder name at the export root.</summary>
    public string? PlcName { get; set; }

    /// <summary>Name of the hardware Device the PLC software belongs to (rack station).</summary>
    public string? DeviceName { get; set; }

    /// <summary>TypeIdentifier of the PLC module device item — the CPU order number plus firmware
    /// version (e.g. "OrderNumber:6ES7515-2AM02-0AB0/V2.9").</summary>
    public string? TypeIdentifier { get; set; }

    public string? ProjectName { get; set; }

    /// <summary>ProjectBase.Author.</summary>
    public string? ProjectAuthor { get; set; }

    /// <summary>ProjectBase.Comment.</summary>
    public string? ProjectComment { get; set; }

    /// <summary>ProjectBase.Version (TIA project version string).</summary>
    public string? ProjectVersion { get; set; }

    /// <summary>ProjectBase.Copyright.</summary>
    public string? ProjectCopyright { get; set; }

    public DateTimeOffset? ProjectCreationTime { get; set; }
    public DateTimeOffset? ProjectLastModified { get; set; }

    /// <summary>ProjectBase.LastModifiedBy.</summary>
    public string? ProjectLastModifiedBy { get; set; }
}

public sealed class ExportMetadataRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string SiemensTypeName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ExportedFile { get; set; }
    public string? Message { get; set; }
    public string? ProgrammingLanguage { get; set; }
    public string? TiaIdentifier { get; set; }
    public int? Number { get; set; }
    public bool? IsKnowHowProtected { get; set; }
    public DateTimeOffset? CreationDate { get; set; }
    public DateTimeOffset? ModifiedDate { get; set; }
    public DateTimeOffset? CodeModifiedDate { get; set; }
    public DateTimeOffset? InterfaceModifiedDate { get; set; }

    /// <summary>SHA256 (base64url, '=' trimmed) of the normalized exported XML (XmlCompare.Normalize —
    /// export timestamp lines and CR stripped, so only real content changes move it). Null for failed
    /// exports and legacy manifests; sync_export treats null as "needs one re-export".</summary>
    public string? ContentHash { get; set; }

    /// <summary>Canonical "Id=Value;…" TIA fingerprint set (blocks/UDTs only — tag tables have no
    /// FingerprintProvider). sync_export compares these in-memory to detect changes without
    /// exporting; null on legacy manifests, tag tables, and unreadable providers.</summary>
    public string? Fingerprints { get; set; }

    /// <summary>Named fingerprint values written as the structured <c>fingerprints</c> JSON
    /// object. Legacy string manifests are parsed into this property on read.</summary>
    public FingerprintSet? FingerprintComponents { get; set; }
}

internal static class ExportMetadataJsonSerializer
{
    public static string Serialize(ExportMetadataDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        WriteProperty(builder, 1, "schemaVersion", document.SchemaVersion, appendComma: true);
        WriteProperty(builder, 1, "exportStartedUtc", document.ExportStartedUtc.ToString("O"), appendComma: true);
        WriteProperty(builder, 1, "exportFinishedUtc", document.ExportFinishedUtc.ToString("O"), appendComma: true);
        WriteProperty(builder, 1, "exportRoot", document.ExportRoot, appendComma: true);
        WriteDevice(builder, document.Device);
        Indent(builder, 1).AppendLine("\"components\": [");

        for (var index = 0; index < document.Components.Count; index++)
        {
            WriteRecord(builder, document.Components[index], index < document.Components.Count - 1);
        }

        Indent(builder, 1).AppendLine("]");
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>Upsert read path: tolerant parse of a manifest previously written by <see cref="Serialize"/>.</summary>
    public static ExportMetadataDocument Deserialize(string json)
    {
        using var jsonDocument = JsonDocument.Parse(json);
        var root = jsonDocument.RootElement;
        var document = new ExportMetadataDocument
        {
            SchemaVersion = GetString(root, "schemaVersion") ?? "1.0",
            ExportStartedUtc = GetDate(root, "exportStartedUtc") ?? DateTimeOffset.UtcNow,
            ExportFinishedUtc = GetDate(root, "exportFinishedUtc") ?? DateTimeOffset.UtcNow,
            ExportRoot = GetString(root, "exportRoot") ?? string.Empty,
            Device = GetDevice(root),
        };
        if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in components.EnumerateArray())
            {
                var fingerprints = GetString(element, "fingerprints");
                var fingerprintComponents = GetFingerprintSet(element, "fingerprints")
                    ?? FingerprintSet.Parse(fingerprints);
                document.Components.Add(new ExportMetadataRecord
                {
                    Id = GetString(element, "id") ?? string.Empty,
                    Name = GetString(element, "name") ?? string.Empty,
                    SourcePath = GetString(element, "sourcePath") ?? string.Empty,
                    Category = GetString(element, "category") ?? string.Empty,
                    Folder = GetString(element, "folder") ?? string.Empty,
                    SiemensTypeName = GetString(element, "siemensTypeName") ?? string.Empty,
                    Status = GetString(element, "status") ?? string.Empty,
                    ExportedFile = GetString(element, "exportedFile"),
                    Message = GetString(element, "message"),
                    ProgrammingLanguage = GetString(element, "programmingLanguage"),
                    TiaIdentifier = GetString(element, "tiaIdentifier"),
                    Number = GetInt(element, "number"),
                    IsKnowHowProtected = GetBool(element, "isKnowHowProtected"),
                    CreationDate = GetDate(element, "creationDate"),
                    ModifiedDate = GetDate(element, "modifiedDate"),
                    CodeModifiedDate = GetDate(element, "codeModifiedDate"),
                    InterfaceModifiedDate = GetDate(element, "interfaceModifiedDate"),
                    ContentHash = GetString(element, "contentHash"),
                    Fingerprints = fingerprintComponents?.ToCanonicalString() ?? fingerprints,
                    FingerprintComponents = fingerprintComponents,
                });
            }
        }
        return document;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static FingerprintSet? GetFingerprintSet(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new FingerprintSet();
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } fingerprint)
            {
                result[property.Name] = fingerprint;
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static DateTimeOffset? GetDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var date)
            ? date
            : null;

    private static DeviceMetadata? GetDevice(JsonElement root)
    {
        if (!root.TryGetProperty("device", out var device) || device.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new DeviceMetadata
        {
            PlcName = GetString(device, "plcName"),
            DeviceName = GetString(device, "deviceName"),
            TypeIdentifier = GetString(device, "typeIdentifier"),
            ProjectName = GetString(device, "projectName"),
            ProjectAuthor = GetString(device, "projectAuthor"),
            ProjectComment = GetString(device, "projectComment"),
            ProjectVersion = GetString(device, "projectVersion"),
            ProjectCopyright = GetString(device, "projectCopyright"),
            ProjectCreationTime = GetDate(device, "projectCreationTime"),
            ProjectLastModified = GetDate(device, "projectLastModified"),
            ProjectLastModifiedBy = GetString(device, "projectLastModifiedBy"),
        };
    }

    private static void WriteDevice(StringBuilder builder, DeviceMetadata? device)
    {
        Indent(builder, 1).Append("\"device\": ");
        if (device is null)
        {
            builder.AppendLine("null,");
            return;
        }

        builder.AppendLine("{");
        WriteProperty(builder, 2, "plcName", device.PlcName, appendComma: true);
        WriteProperty(builder, 2, "deviceName", device.DeviceName, appendComma: true);
        WriteProperty(builder, 2, "typeIdentifier", device.TypeIdentifier, appendComma: true);
        WriteProperty(builder, 2, "projectName", device.ProjectName, appendComma: true);
        WriteProperty(builder, 2, "projectAuthor", device.ProjectAuthor, appendComma: true);
        WriteProperty(builder, 2, "projectComment", device.ProjectComment, appendComma: true);
        WriteProperty(builder, 2, "projectVersion", device.ProjectVersion, appendComma: true);
        WriteProperty(builder, 2, "projectCopyright", device.ProjectCopyright, appendComma: true);
        WriteProperty(builder, 2, "projectCreationTime", device.ProjectCreationTime?.ToString("O"), appendComma: true);
        WriteProperty(builder, 2, "projectLastModified", device.ProjectLastModified?.ToString("O"), appendComma: true);
        WriteProperty(builder, 2, "projectLastModifiedBy", device.ProjectLastModifiedBy, appendComma: false);
        Indent(builder, 1).AppendLine("},");
    }

    private static void WriteRecord(StringBuilder builder, ExportMetadataRecord record, bool appendComma)
    {
        Indent(builder, 2).AppendLine("{");
        WriteProperty(builder, 3, "id", record.Id, appendComma: true);
        WriteProperty(builder, 3, "name", record.Name, appendComma: true);
        WriteProperty(builder, 3, "sourcePath", record.SourcePath, appendComma: true);
        WriteProperty(builder, 3, "category", record.Category, appendComma: true);
        WriteProperty(builder, 3, "folder", record.Folder, appendComma: true);
        WriteProperty(builder, 3, "siemensTypeName", record.SiemensTypeName, appendComma: true);
        WriteProperty(builder, 3, "status", record.Status, appendComma: true);
        WriteProperty(builder, 3, "exportedFile", record.ExportedFile, appendComma: true);
        WriteProperty(builder, 3, "message", record.Message, appendComma: true);
        WriteProperty(builder, 3, "programmingLanguage", record.ProgrammingLanguage, appendComma: true);
        WriteProperty(builder, 3, "tiaIdentifier", record.TiaIdentifier, appendComma: true);
        WriteProperty(builder, 3, "number", record.Number, appendComma: true);
        WriteProperty(builder, 3, "isKnowHowProtected", record.IsKnowHowProtected, appendComma: true);
        WriteProperty(builder, 3, "creationDate", record.CreationDate?.ToString("O"), appendComma: true);
        WriteProperty(builder, 3, "modifiedDate", record.ModifiedDate?.ToString("O"), appendComma: true);
        WriteProperty(builder, 3, "codeModifiedDate", record.CodeModifiedDate?.ToString("O"), appendComma: true);
        WriteProperty(builder, 3, "interfaceModifiedDate", record.InterfaceModifiedDate?.ToString("O"), appendComma: true);
        WriteProperty(builder, 3, "contentHash", record.ContentHash, appendComma: true);
        WriteFingerprints(builder, record.FingerprintComponents ?? FingerprintSet.Parse(record.Fingerprints), record.Fingerprints);
        Indent(builder, 2).Append('}');
        if (appendComma)
        {
            builder.Append(',');
        }

        builder.AppendLine();
    }

    private static void WriteProperty(StringBuilder builder, int indentLevel, string name, string? value, bool appendComma)
    {
        Indent(builder, indentLevel).Append('"').Append(Escape(name)).Append("\": ");
        if (value == null)
        {
            builder.Append("null");
        }
        else
        {
            builder.Append('"').Append(Escape(value)).Append('"');
        }

        AppendCommaAndNewLine(builder, appendComma);
    }

    private static void WriteFingerprints(
        StringBuilder builder,
        FingerprintSet? fingerprints,
        string? legacyValue)
    {
        if (fingerprints is null)
        {
            WriteProperty(builder, 3, "fingerprints", legacyValue, appendComma: false);
            return;
        }

        Indent(builder, 3).AppendLine("\"fingerprints\": {");
        var entries = fingerprints.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < entries.Length; index++)
        {
            WriteProperty(builder, 4, entries[index].Key, entries[index].Value, index < entries.Length - 1);
        }

        Indent(builder, 3).Append('}');
        AppendCommaAndNewLine(builder, appendComma: false);
    }

    private static void WriteProperty(StringBuilder builder, int indentLevel, string name, int? value, bool appendComma)
    {
        Indent(builder, indentLevel).Append('"').Append(Escape(name)).Append("\": ");
        builder.Append(value.HasValue ? value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null");
        AppendCommaAndNewLine(builder, appendComma);
    }

    private static void WriteProperty(StringBuilder builder, int indentLevel, string name, bool? value, bool appendComma)
    {
        Indent(builder, indentLevel).Append('"').Append(Escape(name)).Append("\": ");
        builder.Append(value.HasValue ? value.Value.ToString().ToLowerInvariant() : "null");
        AppendCommaAndNewLine(builder, appendComma);
    }

    private static void AppendCommaAndNewLine(StringBuilder builder, bool appendComma)
    {
        if (appendComma)
        {
            builder.Append(',');
        }

        builder.AppendLine();
    }

    private static StringBuilder Indent(StringBuilder builder, int indentLevel)
    {
        return builder.Append(' ', indentLevel * 2);
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
