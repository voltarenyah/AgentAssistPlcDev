using System.Text;
using System.Text.Json;

namespace Mcp.Engineering.Export;

// Provenance: ported from PlcSourceExporter.Core/ExportMetadata.cs (TIA Add-in project) so the
// mcp-knowledge server can consume both exporters' manifests with one schema. The JSON field
// naming/order and the stable-id formula are byte-for-byte compatible with the reference.
// Adaptations for this codebase: mutable DTOs (single-block export upserts re-read and rewrite
// the manifest), and a System.Text.Json-based Deserialize added for that upsert read path.

/// <summary>metadata.json document — schemaVersion "1.0" (locked 2026-07-18).</summary>
public sealed class ExportMetadataDocument
{
    public string SchemaVersion { get; set; } = "1.0";
    public DateTimeOffset ExportStartedUtc { get; set; }
    public DateTimeOffset ExportFinishedUtc { get; set; }
    public string ExportRoot { get; set; } = string.Empty;
    public List<ExportMetadataRecord> Components { get; set; } = new();
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
        };
        if (root.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in components.EnumerateArray())
            {
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
                });
            }
        }
        return document;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
        WriteProperty(builder, 3, "interfaceModifiedDate", record.InterfaceModifiedDate?.ToString("O"), appendComma: false);
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
