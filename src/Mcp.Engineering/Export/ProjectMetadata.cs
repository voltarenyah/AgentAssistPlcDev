using System.Text;
using System.Text.Json;

namespace Mcp.Engineering.Export;

/// <summary>Project-level metadata.json document at the export root — contains data that is
/// shared across all PLC devices (sourceProjectPath, per-PLC software checksums). This is
/// distinct from the per-device metadata.json (ExportMetadataDocument) which contains the
/// component manifest for a single PLC device.</summary>
public sealed class ProjectMetadataDocument
{
    public string SchemaVersion { get; set; } = "1.0";

    /// <summary>TIA project source path (.ap17) recorded at export time.</summary>
    public string? SourceProjectPath { get; set; }

    /// <summary>Per-PLC software checksums (mapping PLC device name → checksum). Used by
    /// sync_export's checksum gate on a per-device basis.</summary>
    public Dictionary<string, string> PlcSoftwareChecksums { get; set; } = new();

    /// <summary>Ordered list of PLC device names in this project. Updated by rebuild_export and
    /// sync_export when new devices are discovered. Allows the frontend to list devices offline.</summary>
    public List<string> PlcDevices { get; set; } = new();
}

/// <summary>Read/write helpers for project-level metadata.json at the export root.</summary>
public static class ProjectMetadata
{
    public const string FileName = "metadata.json";

    /// <summary>Read the project metadata at <paramref name="projectRoot"/>. Returns a fresh
    /// default when the file is missing or unparseable (callers treat that as "no baseline").</summary>
    public static ProjectMetadataDocument Read(string projectRoot)
    {
        var path = Path.Combine(projectRoot, FileName);
        if (!File.Exists(path))
            return new ProjectMetadataDocument();

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var result = new ProjectMetadataDocument
            {
                SchemaVersion = TryGetString(root, "schemaVersion") ?? "1.0",
                SourceProjectPath = TryGetString(root, "sourceProjectPath"),
            };

            if (root.TryGetProperty("plcSoftwareChecksum", out var chk) && chk.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in chk.EnumerateObject())
                {
                    if (kv.Value.ValueKind == JsonValueKind.String)
                        result.PlcSoftwareChecksums[kv.Name] = kv.Value.GetString()!;
                }
            }
            // Legacy single-string checksum field migration
            else if (root.TryGetProperty("plcSoftwareChecksum", out var legacyChk) && legacyChk.ValueKind == JsonValueKind.String)
            {
                var value = legacyChk.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    // Discover device names from subdirectories
                    foreach (var subDir in Directory.EnumerateDirectories(projectRoot))
                    {
                        var devMeta = Path.Combine(subDir, ExportManifest.MetadataFileName);
                        if (File.Exists(devMeta))
                            result.PlcSoftwareChecksums[Path.GetFileName(subDir)!] = value;
                    }
                }
            }

            // Parse device list
            if (root.TryGetProperty("plcDevices", out var devices) && devices.ValueKind == JsonValueKind.Array)
            {
                foreach (var dev in devices.EnumerateArray())
                {
                    if (dev.ValueKind == JsonValueKind.String)
                        result.PlcDevices.Add(dev.GetString()!);
                }
            }

            return result;
        }
        catch
        {
            return new ProjectMetadataDocument();
        }
    }

    /// <summary>Write or overwrite the project metadata at <paramref name="projectRoot"/>.
    /// Merges with any existing data so concurrent collections are safe.</summary>
    public static void Write(string projectRoot, ProjectMetadataDocument document)
    {
        var path = Path.Combine(projectRoot, FileName);
        Directory.CreateDirectory(projectRoot);

        // Merge with existing to preserve fields we don't directly set
        var existing = Read(projectRoot);
        var merged = new ProjectMetadataDocument
        {
            SchemaVersion = document.SchemaVersion ?? existing.SchemaVersion,
            SourceProjectPath = document.SourceProjectPath ?? existing.SourceProjectPath,
            PlcSoftwareChecksums = existing.PlcSoftwareChecksums,
            PlcDevices = document.PlcDevices.Count > 0 ? document.PlcDevices : existing.PlcDevices,
        };

        // Merge checksums — new values win
        foreach (var kv in document.PlcSoftwareChecksums)
            merged.PlcSoftwareChecksums[kv.Key] = kv.Value;

        File.WriteAllText(path, Serialize(merged));
    }

    /// <summary>Set the sourceProjectPath field — used by the API host's RecordSourcePath.</summary>
    public static void SetSourceProjectPath(string projectRoot, string sourcePath)
    {
        var doc = Read(projectRoot);
        if (doc.SourceProjectPath == sourcePath)
            return;
        doc.SourceProjectPath = sourcePath;
        Write(projectRoot, doc);
    }

    /// <summary>Update the checksum for a specific PLC device.</summary>
    public static void SetPlcSoftwareChecksum(string projectRoot, string plcName, string? checksum)
    {
        var doc = Read(projectRoot);
        if (checksum is null)
            doc.PlcSoftwareChecksums.Remove(plcName);
        else
            doc.PlcSoftwareChecksums[plcName] = checksum;
        Write(projectRoot, doc);
    }

    /// <summary>Get the stored checksum for a specific PLC device; null when not found.</summary>
    public static string? GetPlcSoftwareChecksum(string projectRoot, string plcName)
    {
        var doc = Read(projectRoot);
        return doc.PlcSoftwareChecksums.TryGetValue(plcName, out var checksum) ? checksum : null;
    }

    private static string Serialize(ProjectMetadataDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        WriteProperty(builder, 1, "schemaVersion", document.SchemaVersion, appendComma: true);
        WriteProperty(builder, 1, "sourceProjectPath", document.SourceProjectPath, appendComma: true);

        // plcDevices array
        Indent(builder, 1).Append("\"plcDevices\": [");
        for (var i = 0; i < document.PlcDevices.Count; i++)
        {
            builder.Append('"').Append(Escape(document.PlcDevices[i])).Append('"');
            if (i < document.PlcDevices.Count - 1)
                builder.Append(", ");
        }
        builder.AppendLine("],");

        Indent(builder, 1).AppendLine("\"plcSoftwareChecksum\": {");
        var entries = document.PlcSoftwareChecksums.ToArray();
        for (var i = 0; i < entries.Length; i++)
        {
            Indent(builder, 2).Append('"').Append(Escape(entries[i].Key)).Append("\": ");
            builder.Append('"').Append(Escape(entries[i].Value)).Append('"');
            if (i < entries.Length - 1)
                builder.Append(',');
            builder.AppendLine();
        }
        Indent(builder, 1).Append('}');
        builder.AppendLine();

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void WriteProperty(StringBuilder builder, int indentLevel, string name, string? value, bool appendComma)
    {
        Indent(builder, indentLevel).Append('"').Append(Escape(name)).Append("\": ");
        if (value == null)
            builder.Append("null");
        else
            builder.Append('"').Append(Escape(value)).Append('"');
        if (appendComma)
            builder.Append(',');
        builder.AppendLine();
    }

    private static StringBuilder Indent(StringBuilder builder, int indentLevel) =>
        builder.Append(' ', indentLevel * 2);

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(character))
                        builder.Append("\\u").Append(((int)character).ToString("x4"));
                    else
                        builder.Append(character);
                    break;
            }
        }
        return builder.ToString();
    }

    private static string? TryGetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
