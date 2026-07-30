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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
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
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? SourcePath { get; set; }

    public string? Category { get; set; }

    public string? Status { get; set; }

    public string? ExportedFile { get; set; }

    public string? ContentHash { get; set; }
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

    public static ExportFolderImportResult Import(string exportRoot, Action<string>? progress = null, string deviceName = "")
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

            try
            {
                var componentImport = ImportComponent(
                    fullRoot,
                    component,
                    graph,
                    project,
                    deviceName);
                if (componentImport == null)
                {
                    warnings.Add($"skipped {relativeFile}: unsupported category '{component.Category ?? string.Empty}'");
                    continue;
                }

                graph.RegisterComponentImport(componentImport);
                imported++;
            }
            catch (IOException ex)
            {
                warnings.Add($"skipped {relativeFile}: {ex.Message}");
            }
            catch (ManifestInvalidException ex)
            {
                warnings.Add($"skipped {relativeFile}: {ex.Message}");
            }
            catch (ComponentIdentityMismatchException ex)
            {
                warnings.Add($"skipped {relativeFile}: {ex.Message}");
            }
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

        TiaXmlSemanticGraphImporter.LinkSymbolsToDbMembers(graph);
        return new ExportFolderImportResult(graph, diskFiles.Length, imported, warnings, "manifest");
    }

    public static ExportFolderImportResult ImportComponent(
        string exportRoot,
        string relativePath,
        string deviceName = "")
    {
        if (string.IsNullOrWhiteSpace(exportRoot))
        {
            throw new ArgumentException("Export root is required.", nameof(exportRoot));
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Component path is required.", nameof(relativePath));
        }

        var fullRoot = Path.GetFullPath(exportRoot);
        var document = ReadManifest(fullRoot);
        var normalizedPath = NormalizeSeparators(relativePath);
        var component = (document.Components ?? new List<ComponentMetadataRecordDto>())
            .SingleOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate.ExportedFile) &&
                string.Equals(
                    NormalizeSeparators(candidate.ExportedFile!),
                    normalizedPath,
                    StringComparison.OrdinalIgnoreCase));
        if (component == null)
        {
            throw new ComponentIdentityMismatchException(
                $"Component path '{NormalizeRelativePath(relativePath)}' is not present in metadata.json.");
        }

        var graph = new SemanticPlcGraph();
        var project = ExportFolderCrawler.CreateProjectNode(exportRoot, fullRoot);
        graph.UpsertNode(project);
        var imported = ImportComponent(fullRoot, component, graph, project, deviceName)
            ?? throw new ManifestInvalidException(
                $"component '{relativePath}' has unsupported category '{component.Category ?? string.Empty}'");
        graph.RegisterComponentImport(imported);
        return new ExportFolderImportResult(
            graph,
            filesFound: 1,
            filesImported: 1,
            Array.Empty<string>(),
            "manifest");
    }

    private static ComponentImport? ImportComponent(
        string fullRoot,
        ComponentMetadataRecordDto component,
        SemanticPlcGraph graph,
        SemanticGraphNode project,
        string deviceName)
    {
        var relativeFile = NormalizeSeparators(component.ExportedFile!);
        var fullPath = ResolveComponentPath(fullRoot, relativeFile);
        var bytes = File.ReadAllBytes(fullPath);
        var xml = DecodeXml(bytes);
        var name = component.Name ?? string.Empty;
        var category = component.Category ?? string.Empty;
        ValidateXmlIdentity(xml, relativeFile, name, category);

        SemanticPlcGraph.GraphTouches touches;
        if (IsProgramBlockCategory(category))
        {
            touches = graph.CaptureTouches(() =>
            {
                TiaXmlSemanticGraphImporter.ImportBlockXml(
                    xml,
                    new ProgramBlockComponent(name, category, component.SourcePath ?? string.Empty, relativeFile),
                    graph,
                    deviceName);
                TiaXmlSemanticGraphImporter.AddEdgeIfTargetExists(
                    graph,
                    project.Id,
                    TiaXmlSemanticGraphImporter.BlockId(deviceName, name),
                    SemanticRelationshipType.Contains);
            });
        }
        else if (string.Equals(category, "DB", StringComparison.OrdinalIgnoreCase))
        {
            touches = graph.CaptureTouches(() =>
            {
                TiaXmlSemanticGraphImporter.ImportDbXml(
                    xml,
                    relativeFile,
                    component.SourcePath ?? string.Empty,
                    graph,
                    deviceName);
                TiaXmlSemanticGraphImporter.AddEdgeIfTargetExists(
                    graph,
                    project.Id,
                    TiaXmlSemanticGraphImporter.DbId(deviceName, name),
                    SemanticRelationshipType.Contains);
            });
        }
        else if (string.Equals(category, "UDT", StringComparison.OrdinalIgnoreCase))
        {
            touches = graph.CaptureTouches(() =>
            {
                TiaXmlSemanticGraphImporter.ImportUdtXml(
                    xml,
                    relativeFile,
                    component.SourcePath ?? string.Empty,
                    graph,
                    deviceName);
                TiaXmlSemanticGraphImporter.AddEdgeIfTargetExists(
                    graph,
                    project.Id,
                    TiaXmlSemanticGraphImporter.UdtId(deviceName, name),
                    SemanticRelationshipType.Contains);
            });
        }
        else if (string.Equals(category, "Tags", StringComparison.OrdinalIgnoreCase))
        {
            touches = graph.CaptureTouches(() =>
                TiaXmlSemanticGraphImporter.ImportTagTableXml(
                    xml,
                    relativeFile,
                    component.SourcePath ?? string.Empty,
                    graph,
                    deviceName));
        }
        else
        {
            return null;
        }

        var normalizedRelativePath = NormalizeRelativePath(relativeFile);
        if (!string.IsNullOrEmpty(deviceName))
        {
            normalizedRelativePath = $"{deviceName}/{normalizedRelativePath}";
        }

        var rawComponentKey = string.IsNullOrWhiteSpace(component.Id)
            ? $"path:{NormalizeRelativePath(relativeFile)}"
            : component.Id!;
        var componentKey = string.IsNullOrEmpty(deviceName)
            ? rawComponentKey
            : $"{deviceName}/{rawComponentKey}";
        var contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ComponentImport(
            componentKey,
            normalizedRelativePath,
            contentHash,
            touches.NodeIds,
            touches.EdgeIds);
    }

    private static string ResolveComponentPath(string fullRoot, string relativeFile)
    {
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativeFile));
        var rootPrefix = fullRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(
                rootPrefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ManifestInvalidException(
                $"component path '{relativeFile}' resolves outside the export root");
        }

        return fullPath;
    }

    private static string DecodeXml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void ValidateXmlIdentity(
        string xml,
        string relativeFile,
        string expectedName,
        string expectedCategory)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new ManifestInvalidException(
                $"component '{NormalizeRelativePath(relativeFile)}' contains malformed XML: {ex.Message}");
        }

        var contentRoot = document.Root;
        if (contentRoot != null &&
            !contentRoot.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal))
        {
            contentRoot = contentRoot
                .Elements()
                .FirstOrDefault(element => element.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal));
        }

        var actualName = contentRoot?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "AttributeList")?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Name")?
            .Value
            .Trim();
        var rootElement = contentRoot?.Name.LocalName ?? string.Empty;
        if (contentRoot == null ||
            !CategoryMatchesRoot(expectedCategory, rootElement) ||
            !string.Equals(actualName, expectedName, StringComparison.Ordinal))
        {
            throw new ComponentIdentityMismatchException(
                $"Component path '{NormalizeRelativePath(relativeFile)}' declares '{actualName ?? "(missing)"}' ({rootElement}), " +
                $"but metadata.json identifies '{expectedName}' ({expectedCategory}).");
        }
    }

    private static bool CategoryMatchesRoot(string category, string rootElement)
    {
        if (IsProgramBlockCategory(category))
        {
            return string.Equals(rootElement, $"SW.Blocks.{category}", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(category, "DB", StringComparison.OrdinalIgnoreCase))
        {
            return rootElement is "SW.Blocks.GlobalDB"
                or "SW.Blocks.InstanceDB"
                or "SW.Blocks.ArrayDB"
                or "SW.Blocks.DB";
        }

        if (string.Equals(category, "UDT", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(rootElement, "SW.Types.PlcStruct", StringComparison.Ordinal);
        }

        return string.Equals(category, "Tags", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rootElement, "SW.Tags.PlcTagTable", StringComparison.Ordinal);
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
        return relativeFile
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
    }

    private static string NormalizeRelativePath(string relativeFile)
    {
        return relativeFile
            .Replace('\\', '/')
            .Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool IsProgramBlockCategory(string? category)
    {
        return string.Equals(category, "OB", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(category, "FC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(category, "FB", StringComparison.OrdinalIgnoreCase);
    }
}
