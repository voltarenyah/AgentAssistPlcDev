// Ported from PlcSourceExporter (src/PlcSourceExporter.Core/SemanticPlcGraph.cs — ImportExportRoot/
// LoadExportedComponents/IsProgramBlockCategory; reader DTOs from ProgramBlockComponentCatalog.cs) —
// adapted for mcp-knowledge; keep changes minimal to ease future re-syncs.
// Adaptations vs the reference:
// - DataContractJsonSerializer replaced with System.Text.Json; the reader DTO keeps only the fields
//   the import consumes (plus schemaVersion for a mismatch warning), unknown JSON fields are ignored.
// - UDT/Tags categories are skipped with a "deferred to a later step" warning (buildnote/plan/mcp-knowledge.md §2.5).
// - Reconciliation added (2026-07-18 manifest decision): an entry marked Exported whose file is missing,
//   or an on-disk *.xml not referenced by any manifest entry, produces a warning.
// - exportedFile accepts both '/' and '\' separators (normalized to the platform separator).
// - Malformed/unreadable metadata.json throws ManifestInvalidException (surfaced as MANIFEST_INVALID
//   by the tool) instead of being treated as "no manifest".
using System.Text.Json;
using Mcp.Knowledge.Graph;
using Mcp.Knowledge.Parsing;

namespace Mcp.Knowledge.Import;

public sealed class ComponentMetadataDocumentDto
{
    public string? SchemaVersion { get; set; }

    public List<ComponentMetadataRecordDto>? Components { get; set; }
}

public sealed class ComponentMetadataRecordDto
{
    public string? Name { get; set; }

    public string? SourcePath { get; set; }

    public string? Category { get; set; }

    public string? Status { get; set; }

    public string? ExportedFile { get; set; }
}

public sealed class ManifestInvalidException : Exception
{
    public ManifestInvalidException(string message)
        : base(message)
    {
    }
}

public static class ManifestImporter
{
    public const string MetadataFileName = "metadata.json";
    public const string ExpectedSchemaVersion = "1.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static ExportFolderImportResult Import(string exportRoot, Action<string>? progress = null)
    {
        var fullRoot = Path.GetFullPath(exportRoot);
        var warnings = new List<string>();
        var document = ReadManifest(fullRoot);

        if (!string.Equals(document.SchemaVersion, ExpectedSchemaVersion, StringComparison.Ordinal))
        {
            warnings.Add(
                $"metadata.json schemaVersion is '{document.SchemaVersion ?? "(missing)"}', expected '{ExpectedSchemaVersion}' — importing anyway");
        }

        var components = (document.Components ?? new List<ComponentMetadataRecordDto>())
            .Where(component =>
                string.Equals(component.Status, "Exported", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(component.ExportedFile))
            .OrderBy(component => component.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Reconciliation baseline: every exportedFile the manifest mentions, in any status.
        var referencedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in document.Components ?? new List<ComponentMetadataRecordDto>())
        {
            if (!string.IsNullOrWhiteSpace(component.ExportedFile))
            {
                referencedFiles.Add(NormalizeSeparators(component.ExportedFile!));
            }
        }

        var graph = new SemanticPlcGraph();
        var project = ExportFolderCrawler.CreateProjectNode(exportRoot, fullRoot);
        graph.UpsertNode(project);

        var imported = 0;
        var processed = 0;
        foreach (var component in components)
        {
            processed++;
            if (progress != null && (processed % 100 == 0 || processed == components.Length))
            {
                progress($"ingest_source: {processed}/{components.Length} files (manifest)");
            }

            var relativeFile = NormalizeSeparators(component.ExportedFile!);
            if (!File.Exists(Path.Combine(fullRoot, relativeFile)))
            {
                warnings.Add(
                    $"manifest entry '{component.Name}' ({component.Category}) is marked Exported but its file is missing: {relativeFile}");
                continue;
            }

            var name = component.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                warnings.Add($"skipped {relativeFile}: manifest entry has no name");
                continue;
            }

            string xml;
            try
            {
                xml = File.ReadAllText(Path.Combine(fullRoot, relativeFile));
            }
            catch (IOException ex)
            {
                warnings.Add($"skipped {relativeFile}: {ex.Message}");
                continue;
            }

            var category = component.Category ?? string.Empty;
            if (IsProgramBlockCategory(category))
            {
                TiaXmlSemanticGraphImporter.ImportBlockXml(
                    xml,
                    new ProgramBlockComponent(name, category, component.SourcePath ?? string.Empty, relativeFile),
                    graph);
                TiaXmlSemanticGraphImporter.AddEdgeIfTargetExists(
                    graph, project.Id, TiaXmlSemanticGraphImporter.BlockId(name), SemanticRelationshipType.Contains);
                imported++;
                continue;
            }

            if (string.Equals(category, "DB", StringComparison.OrdinalIgnoreCase))
            {
                TiaXmlSemanticGraphImporter.ImportDbXml(xml, relativeFile, component.SourcePath ?? string.Empty, graph);
                TiaXmlSemanticGraphImporter.AddEdgeIfTargetExists(
                    graph, project.Id, TiaXmlSemanticGraphImporter.DbId(name), SemanticRelationshipType.Contains);
                imported++;
                continue;
            }

            if (string.Equals(category, "UDT", StringComparison.OrdinalIgnoreCase))
            {
                TiaXmlSemanticGraphImporter.ImportUdtXml(xml, relativeFile, component.SourcePath ?? string.Empty, graph);
                TiaXmlSemanticGraphImporter.AddEdgeIfTargetExists(
                    graph, project.Id, TiaXmlSemanticGraphImporter.UdtId(name), SemanticRelationshipType.Contains);
                imported++;
                continue;
            }

            if (string.Equals(category, "Tags", StringComparison.OrdinalIgnoreCase))
            {
                // Reference behaviour: tag tables get no project CONTAINS edge (tags float freely).
                TiaXmlSemanticGraphImporter.ImportTagTableXml(xml, relativeFile, component.SourcePath ?? string.Empty, graph);
                imported++;
                continue;
            }

            warnings.Add($"skipped {relativeFile}: unsupported category '{category}'");
        }

        var diskFiles = Directory
            .EnumerateFiles(fullRoot, "*.xml", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(fullRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        foreach (var diskFile in diskFiles)
        {
            if (!referencedFiles.Contains(diskFile))
            {
                warnings.Add($"not in manifest, ignored: {diskFile}");
            }
        }

        return new ExportFolderImportResult(graph, diskFiles.Length, imported, warnings, "manifest");
    }

    private static ComponentMetadataDocumentDto ReadManifest(string fullRoot)
    {
        var metadataPath = Path.Combine(fullRoot, MetadataFileName);
        string json;
        try
        {
            json = File.ReadAllText(metadataPath);
        }
        catch (IOException ex)
        {
            throw new ManifestInvalidException($"metadata.json in '{fullRoot}' could not be read: {ex.Message}");
        }

        try
        {
            return JsonSerializer.Deserialize<ComponentMetadataDocumentDto>(json, JsonOptions)
                ?? new ComponentMetadataDocumentDto();
        }
        catch (JsonException ex)
        {
            throw new ManifestInvalidException($"metadata.json in '{fullRoot}' is not valid JSON: {ex.Message}");
        }
    }

    private static string NormalizeSeparators(string relativeFile)
    {
        return relativeFile.Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool IsProgramBlockCategory(string? category)
    {
        return string.Equals(category, "OB", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(category, "FC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(category, "FB", StringComparison.OrdinalIgnoreCase);
    }
}
