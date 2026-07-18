// New code for mcp-knowledge (buildnote/plan/mcp-knowledge.md §3, §7) — not a port.
// Manifest-first dispatcher (2026-07-18 decision): when <exportRoot>/metadata.json exists the import is
// driven by ManifestImporter; otherwise this root-element folder crawl is the fallback. The crawl
// classifies each exported file by its SW.* root element and feeds the ported per-category import
// methods (Graph/TiaXmlSemanticGraphImporter).
using System.Xml;
using System.Xml.Linq;
using Mcp.Knowledge.Graph;
using Mcp.Knowledge.Parsing;

namespace Mcp.Knowledge.Import;

public sealed class ExportFolderImportResult
{
    public ExportFolderImportResult(SemanticPlcGraph graph, int filesFound, int filesImported, IReadOnlyList<string> warnings, string source)
    {
        Graph = graph;
        FilesFound = filesFound;
        FilesImported = filesImported;
        Warnings = warnings;
        Source = source;
    }

    public SemanticPlcGraph Graph { get; }

    public int FilesFound { get; }

    public int FilesImported { get; }

    public IReadOnlyList<string> Warnings { get; }

    /// <summary>"manifest" when driven by metadata.json, "crawl" for the root-element folder crawl.</summary>
    public string Source { get; }
}

public static class ExportFolderCrawler
{
    private const string ProgramBlockPrefix = "SW.Blocks.";

    public static ExportFolderImportResult Import(string exportRoot)
    {
        if (string.IsNullOrWhiteSpace(exportRoot))
        {
            throw new ArgumentException("Export root is required.", nameof(exportRoot));
        }

        if (!Directory.Exists(exportRoot))
        {
            throw new DirectoryNotFoundException($"Export root '{exportRoot}' was not found.");
        }

        return File.Exists(Path.Combine(exportRoot, ManifestImporter.MetadataFileName))
            ? ManifestImporter.Import(exportRoot)
            : ImportByCrawl(exportRoot);
    }

    internal static SemanticGraphNode CreateProjectNode(string exportRoot, string fullRoot)
    {
        var projectName = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(projectName))
        {
            projectName = exportRoot;
        }

        return new SemanticGraphNode(
            $"project:{projectName}",
            SemanticNodeKind.Project,
            projectName,
            new Dictionary<string, string> { ["exportRoot"] = exportRoot });
    }

    private static ExportFolderImportResult ImportByCrawl(string exportRoot)
    {
        var fullRoot = Path.GetFullPath(exportRoot);
        var relativeFiles = Directory
            .EnumerateFiles(fullRoot, "*.xml", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(fullRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var candidates = new List<ImportCandidate>();
        var warnings = new List<string>();
        foreach (var relativeFile in relativeFiles)
        {
            var candidate = TryClassify(fullRoot, relativeFile, warnings);
            if (candidate != null)
            {
                candidates.Add(candidate);
            }
        }

        // Duplicate rule (§3): same block identity (root element + name) in several files keeps the
        // shallowest relative path, ties broken alphabetically; losers are reported, not imported.
        var winners = new List<ImportCandidate>();
        foreach (var group in candidates.GroupBy(candidate => candidate.Identity))
        {
            var ordered = group
                .OrderBy(candidate => candidate.Depth)
                .ThenBy(candidate => candidate.RelativeFile, StringComparer.Ordinal)
                .ToArray();
            winners.Add(ordered[0]);
            foreach (var duplicate in ordered.Skip(1))
            {
                warnings.Add(
                    $"skipped duplicate: {duplicate.RelativeFile} ('{duplicate.Name}' {duplicate.RootElement} already imported from {ordered[0].RelativeFile})");
            }
        }

        var graph = new SemanticPlcGraph();
        var project = CreateProjectNode(exportRoot, fullRoot);
        graph.UpsertNode(project);

        var imported = 0;
        foreach (var winner in winners.OrderBy(candidate => candidate.RelativeFile, StringComparer.Ordinal))
        {
            var sourcePath = Path.GetDirectoryName(winner.RelativeFile) ?? string.Empty;
            if (winner.Category != null)
            {
                TiaXmlSemanticGraphImporter.ImportBlockXml(
                    winner.Xml,
                    new ProgramBlockComponent(winner.Name, winner.Category, sourcePath, winner.RelativeFile),
                    graph);
                TiaXmlSemanticGraphImporter.AddEdgeIfTargetExists(
                    graph, project.Id, TiaXmlSemanticGraphImporter.BlockId(winner.Name), SemanticRelationshipType.Contains);
            }
            else
            {
                TiaXmlSemanticGraphImporter.ImportDbXml(winner.Xml, winner.RelativeFile, sourcePath, graph);
                TiaXmlSemanticGraphImporter.AddEdgeIfTargetExists(
                    graph, project.Id, TiaXmlSemanticGraphImporter.DbId(winner.Name), SemanticRelationshipType.Contains);
            }

            imported++;
        }

        return new ExportFolderImportResult(graph, relativeFiles.Length, imported, warnings, "crawl");
    }

    private static ImportCandidate? TryClassify(string fullRoot, string relativeFile, IList<string> warnings)
    {
        string xml;
        XDocument document;
        try
        {
            xml = File.ReadAllText(Path.Combine(fullRoot, relativeFile));
            document = XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            warnings.Add($"skipped {relativeFile}: malformed XML ({ex.Message})");
            return null;
        }
        catch (IOException ex)
        {
            warnings.Add($"skipped {relativeFile}: {ex.Message}");
            return null;
        }

        var contentRoot = document.Root;
        if (contentRoot != null && !contentRoot.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal))
        {
            // TIA exports wrap the payload in <Document>; classify by the first SW.* child.
            contentRoot = contentRoot
                .Elements()
                .FirstOrDefault(element => element.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal));
        }

        if (contentRoot == null)
        {
            warnings.Add($"skipped {relativeFile}: no SW.* content element found");
            return null;
        }

        var rootElement = contentRoot.Name.LocalName;
        switch (rootElement)
        {
            case "SW.Types.PlcStruct":
                warnings.Add($"skipped {relativeFile}: UDT import is deferred to a later step");
                return null;
            case "SW.Tags.PlcTagTable":
                warnings.Add($"skipped {relativeFile}: tag-table import is deferred to a later step");
                return null;
        }

        var isProgramBlock =
            rootElement == "SW.Blocks.OB" ||
            rootElement == "SW.Blocks.FB" ||
            rootElement == "SW.Blocks.FC";
        var isDataBlock =
            rootElement == "SW.Blocks.GlobalDB" ||
            rootElement == "SW.Blocks.InstanceDB" ||
            rootElement == "SW.Blocks.ArrayDB" ||
            rootElement == "SW.Blocks.DB";
        if (!isProgramBlock && !isDataBlock)
        {
            warnings.Add($"skipped {relativeFile}: unsupported root element '{rootElement}'");
            return null;
        }

        var name = GetAttributeListValue(contentRoot, "Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            warnings.Add($"skipped {relativeFile}: no <Name> entry in AttributeList of '{rootElement}'");
            return null;
        }

        return new ImportCandidate(
            relativeFile,
            xml,
            name,
            rootElement,
            isProgramBlock ? rootElement.Substring(ProgramBlockPrefix.Length) : null,
            relativeFile.Count(character => character == '/' || character == '\\'));
    }

    private static string GetAttributeListValue(XElement element, string name)
    {
        return element
            .Elements()
            .FirstOrDefault(child => child.Name.LocalName == "AttributeList")
            ?.Elements()
            .FirstOrDefault(child => child.Name.LocalName == name)
            ?.Value
            .Trim() ?? string.Empty;
    }

    private sealed class ImportCandidate
    {
        public ImportCandidate(string relativeFile, string xml, string name, string rootElement, string? category, int depth)
        {
            RelativeFile = relativeFile;
            Xml = xml;
            Name = name;
            RootElement = rootElement;
            Category = category;
            Depth = depth;
            Identity = rootElement + "\n" + name;
        }

        public string RelativeFile { get; }

        public string Xml { get; }

        public string Name { get; }

        public string RootElement { get; }

        /// <summary>Block category (OB/FB/FC) for program blocks; null for data blocks.</summary>
        public string? Category { get; }

        public int Depth { get; }

        /// <summary>Duplicate-detection identity: content root element + block name.</summary>
        public string Identity { get; }
    }
}
