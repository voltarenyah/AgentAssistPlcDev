using System.Security.Cryptography;
using System.Text;
using Contracts.Engineering;
using Siemens.Engineering.SW.Blocks;

namespace Mcp.Engineering.Export;

// Provenance: writer-side counterpart of the PlcSourceExporter.Core/ExportMetadata.cs port in
// ExportMetadata.cs. CreateStableId and ToRelativePath mirror the reference implementation
// (stable id = base64url(SHA256("category|sourcePath")), '=' padding trimmed) so ids are
// interoperable across both exporters; the record mapping is specific to this adapter.

/// <summary>
/// Builds and writes the metadata.json manifest next to exported block XML (§5.2 evidence
/// contract with mcp-knowledge). export_all_blocks writes one manifest per export root;
/// export_block upserts a single record into the existing manifest (or creates a fresh one).
/// </summary>
internal static class ExportManifest
{
    public const string MetadataFileName = "metadata.json";

    /// <summary>OB/FB/FC stay their own category; every DB flavor (GlobalDB/InstanceDB/ArrayDB) is "DB".</summary>
    public static string CategoryOf(PlcBlock block) => block.GetType().Name switch
    {
        "GlobalDB" or "InstanceDB" or "ArrayDB" => "DB",
        var name => name,
    };

    /// <summary>Export subfolder for a category — program blocks under Blocks/, data blocks under DB/.</summary>
    public static string FolderFor(string category) => category == "DB" ? "DB" : "Blocks";

    /// <summary>'/'-joined group path + block name (just the name at the root). Stable across runs — the manifest id derives from it.</summary>
    public static string SourcePathOf(PlcBlock block, string? groupPath) =>
        groupPath is null ? block.Name : groupPath + "/" + block.Name;

    public static ExportMetadataRecord CreateRecord(PlcBlock block, string? groupPath, string exportRoot, ExportResult result)
    {
        var category = CategoryOf(block);
        var sourcePath = SourcePathOf(block, groupPath);

        // Openness metadata access can fail (e.g. know-how-protected blocks) — degrade to null
        // instead of failing the manifest. All four properties exist on PlcBlock in V17
        // (buildnote/bestpractice/openness-v17-api-surface.md §3); CreationDate does not, so it stays null.
        bool? isKnowHowProtected = null;
        DateTimeOffset? modifiedDate = null, codeModifiedDate = null, interfaceModifiedDate = null;
        try
        {
            isKnowHowProtected = block.IsKnowHowProtected;
            modifiedDate = block.ModifiedDate;
            codeModifiedDate = block.CodeModifiedDate;
            interfaceModifiedDate = block.InterfaceModifiedDate;
        }
        catch { }

        var language = block.ProgrammingLanguage.ToString();
        return new ExportMetadataRecord
        {
            Id = CreateStableId(category, sourcePath),
            Name = block.Name,
            SourcePath = sourcePath,
            Category = category,
            Folder = FolderFor(category),
            SiemensTypeName = block.GetType().Name,
            Status = result.Success ? "Exported" : "Failed",
            ExportedFile = result.Success ? ToRelativePath(exportRoot, result.Path) : null,
            Message = result.Success ? null : result.Error,
            ProgrammingLanguage = language == "Undef" ? null : language,
            TiaIdentifier = block.Name,
            Number = block.Number,
            IsKnowHowProtected = isKnowHowProtected,
            CreationDate = null,
            ModifiedDate = modifiedDate,
            CodeModifiedDate = codeModifiedDate,
            InterfaceModifiedDate = interfaceModifiedDate,
        };
    }

    /// <summary>export_all_blocks: fresh manifest covering every block attempted (Exported and Failed).</summary>
    public static void WriteAll(string exportRoot, DateTimeOffset exportStartedUtc, List<ExportMetadataRecord> records)
    {
        var document = new ExportMetadataDocument
        {
            ExportStartedUtc = exportStartedUtc,
            ExportFinishedUtc = DateTimeOffset.UtcNow,
            ExportRoot = exportRoot,
            Components = records,
        };
        Write(exportRoot, document);
    }

    /// <summary>export_block: replace the record with the same id, keep other records, preserve exportStartedUtc.</summary>
    public static void Upsert(string exportRoot, ExportMetadataRecord record)
    {
        var path = Path.Combine(exportRoot, MetadataFileName);
        ExportMetadataDocument document;
        if (File.Exists(path))
        {
            document = ExportMetadataJsonSerializer.Deserialize(File.ReadAllText(path));
            var index = document.Components.FindIndex(r => r.Id == record.Id);
            if (index >= 0)
                document.Components[index] = record;
            else
                document.Components.Add(record);
        }
        else
        {
            document = new ExportMetadataDocument
            {
                ExportStartedUtc = DateTimeOffset.UtcNow,
                ExportRoot = exportRoot,
                Components = { record },
            };
        }
        document.ExportFinishedUtc = DateTimeOffset.UtcNow;
        Write(exportRoot, document);
    }

    private static void Write(string exportRoot, ExportMetadataDocument document) =>
        File.WriteAllText(
            Path.Combine(exportRoot, MetadataFileName),
            ExportMetadataJsonSerializer.Serialize(document));

    private static string CreateStableId(string category, string sourcePath)
    {
        using var sha256 = SHA256.Create();
        var input = $"{category}|{sourcePath}";
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string? ToRelativePath(string exportRoot, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var root = Path.GetFullPath(exportRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(filePath!);
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? fullPath.Substring(root.Length + 1)
            : filePath;
    }
}
