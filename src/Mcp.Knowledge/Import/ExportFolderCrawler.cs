// New code for mcp-knowledge (buildnote/plan/mcp-knowledge.md §3, §7) — not a port.
// Manifest-first dispatcher (2026-07-18 decision): when <exportRoot>/metadata.json exists the import is
// driven by ManifestImporter; otherwise this root-element folder crawl is the fallback. The crawl
// classifies each exported file by its SW.* root element and feeds the ported per-category import
// methods (Graph/TiaXmlSemanticGraphImporter).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    public IReadOnlyList<ComponentImport> Components => Graph.ComponentImports;

    /// <summary>"manifest" when driven by metadata.json, "crawl" for the root-element folder crawl.</summary>
    public string Source { get; }
}

public static class ExportFolderCrawler
{
    private const string ProgramBlockPrefix = "SW.Blocks.";

    public static ExportFolderImportResult Import(string exportRoot, Action<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(exportRoot))
        {
            throw new ArgumentException("Export root is required.", nameof(exportRoot));
        }

        if (!Directory.Exists(exportRoot))
        {
            throw new DirectoryNotFoundException($"Export root '{exportRoot}' was not found.");
        }

        // 1. Per-device subfolders: each subdirectory with its own metadata.json
        var deviceFolders = Directory.EnumerateDirectories(exportRoot)
            .Where(dir => File.Exists(Path.Combine(dir, ManifestImporter.MetadataFileName)))
            .ToArray();

        if (deviceFolders.Length > 0)
        {
            // Import each device individually, then merge into one graph
            var combinedGraph = new SemanticPlcGraph();
            var allWarnings = new List<string>();
            var totalFilesFound = 0;
            var totalFilesImported = 0;
            var fullRoot = Path.GetFullPath(exportRoot);
            var projectNode = CreateProjectNode(exportRoot, fullRoot);
            combinedGraph.UpsertNode(projectNode);

            foreach (var deviceDir in deviceFolders)
            {
                var deviceName = Path.GetFileName(deviceDir);
                var result = ManifestImporter.Import(deviceDir, progress, deviceName);
                // Project node from the device's manifest — skip it (we use the combined one).
                var skippedProjectIds = result.Graph.Nodes
                    .Where(n => string.Equals(n.Kind, SemanticNodeKind.Project, StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.Id)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var node in result.Graph.Nodes)
                    if (!skippedProjectIds.Contains(node.Id))
                        combinedGraph.UpsertNode(node);
                foreach (var edge in result.Graph.Edges)
                {
                    if (skippedProjectIds.Contains(edge.FromNodeId) && skippedProjectIds.Contains(edge.ToNodeId))
                        continue;
                    if (skippedProjectIds.Contains(edge.FromNodeId))
                    {
                        // Rewire: point the CONTAINS edge to the combined project node
                        combinedGraph.UpsertEdge(new SemanticGraphEdge(
                            TiaXmlSemanticGraphImporter.EdgeId(projectNode.Id, edge.ToNodeId, edge.Type, edge.Properties),
                            projectNode.Id, edge.ToNodeId, edge.Type, edge.Properties));
                    }
                    else if (skippedProjectIds.Contains(edge.ToNodeId))
                    {
                        continue; // edge targeting the old project node — drop
                    }
                    else
                    {
                        combinedGraph.UpsertEdge(edge);
                    }
                }
                foreach (var component in result.Components)
                {
                    var rewrittenEdges = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var edgeId in component.EdgeIds)
                    {
                        var edge = result.Graph.Edges.Single(candidate => candidate.Id == edgeId);
                        if (skippedProjectIds.Contains(edge.ToNodeId))
                        {
                            continue;
                        }

                        rewrittenEdges.Add(skippedProjectIds.Contains(edge.FromNodeId)
                            ? TiaXmlSemanticGraphImporter.EdgeId(
                                projectNode.Id,
                                edge.ToNodeId,
                                edge.Type,
                                edge.Properties)
                            : edge.Id);
                    }

                    combinedGraph.RegisterComponentImport(component with
                    {
                        NodeIds = component.NodeIds
                            .Where(nodeId => !skippedProjectIds.Contains(nodeId))
                            .ToHashSet(StringComparer.Ordinal),
                        EdgeIds = rewrittenEdges,
                    });
                }
                totalFilesFound += result.FilesFound;
                totalFilesImported += result.FilesImported;
                allWarnings.AddRange(result.Warnings);
            }

            // CONTAINS edges from the combined project node to each device
            foreach (var deviceDir in deviceFolders)
            {
                var deviceName = Path.GetFileName(deviceDir);
                combinedGraph.UpsertNode(new SemanticGraphNode(
                    $"plc-device:{deviceName}",
                    SemanticNodeKind.PlcDevice,
                    deviceName));
                TiaXmlSemanticGraphImporter.AddEdgeIfTargetExists(
                    combinedGraph, projectNode.Id, $"plc-device:{deviceName}", SemanticRelationshipType.Contains);
            }

            return new ExportFolderImportResult(combinedGraph, totalFilesFound, totalFilesImported, allWarnings, "manifest");
        }

        // 2. Legacy flat export: metadata.json at the project root (pre-device-subfolder structure)
        // that contains a "components" array. After the metadata split (2026-07-24), the project
        // root metadata.json is project-level only (no components) — skip it, fall to crawl.
        var rootMeta = Path.Combine(exportRoot, ManifestImporter.MetadataFileName);
        if (File.Exists(rootMeta) && HasComponents(rootMeta))
        {
            return ManifestImporter.Import(exportRoot, progress);
        }

        // 3. Fall to crawl (legacy no-manifest)
        return ImportByCrawl(exportRoot, progress);
    }

    /// <summary>Cheap check: does the metadata.json at <paramref name="path"/> have a "components"
    /// array? True for legacy device manifests, false for project-level metadata.</summary>
    private static bool HasComponents(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("components", out var comps)
                && comps.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException ex)
        {
            throw new ManifestInvalidException(
                $"metadata.json at '{path}' is not valid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new ManifestInvalidException(
                $"metadata.json at '{path}' could not be read: {ex.Message}");
        }
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

    private static ExportFolderImportResult ImportByCrawl(string exportRoot, Action<string>? progress)
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
            // Extract device name from the first segment of the relative path, if present
            var firstSep = sourcePath.IndexOfAny(new[] { '/', '\\' });
            var deviceName = firstSep > 0 ? sourcePath.Substring(0, firstSep) : "";
            // Also handle the case where the file is directly at a device subfolder root
            if (string.IsNullOrEmpty(deviceName) && sourcePath.Length > 0)
                deviceName = sourcePath;

            var touches = graph.CaptureTouches(() =>
            {
                switch (winner.Kind)
                {
                    case ImportKind.ProgramBlock:
                        TiaXmlSemanticGraphImporter.ImportBlockXml(
                            winner.Xml,
                            new ProgramBlockComponent(winner.Name, winner.Category!, sourcePath, winner.RelativeFile),
                            graph,
                            deviceName);
                        TiaXmlSemanticGraphImporter.AddEdgeIfTargetExists(
                            graph, project.Id, TiaXmlSemanticGraphImporter.BlockId(deviceName, winner.Name), SemanticRelationshipType.Contains);
                        break;
                    case ImportKind.DataBlock:
                        TiaXmlSemanticGraphImporter.ImportDbXml(winner.Xml, winner.RelativeFile, sourcePath, graph, deviceName);
                        TiaXmlSemanticGraphImporter.AddEdgeIfTargetExists(
                            graph, project.Id, TiaXmlSemanticGraphImporter.DbId(deviceName, winner.Name), SemanticRelationshipType.Contains);
                        break;
                    case ImportKind.Udt:
                        TiaXmlSemanticGraphImporter.ImportUdtXml(winner.Xml, winner.RelativeFile, sourcePath, graph, deviceName);
                        TiaXmlSemanticGraphImporter.AddEdgeIfTargetExists(
                            graph, project.Id, TiaXmlSemanticGraphImporter.UdtId(deviceName, winner.Name), SemanticRelationshipType.Contains);
                        break;
                    case ImportKind.TagTable:
                        // Reference behaviour: tag tables get no project CONTAINS edge (tags float freely).
                        TiaXmlSemanticGraphImporter.ImportTagTableXml(winner.Xml, winner.RelativeFile, sourcePath, graph);
                        break;
                }
            });
            var normalizedPath = winner.RelativeFile.Replace('\\', '/');
            graph.RegisterComponentImport(new ComponentImport(
                $"path:{normalizedPath}",
                normalizedPath,
                winner.ContentHash,
                touches.NodeIds,
                touches.EdgeIds));

            imported++;
            if (progress != null && (imported % 100 == 0 || imported == winners.Count))
            {
                progress($"ingest_source: {imported}/{winners.Count} files (crawl)");
            }
        }

        TiaXmlSemanticGraphImporter.LinkSymbolsToDbMembers(graph);
        return new ExportFolderImportResult(graph, relativeFiles.Length, imported, warnings, "crawl");
    }

    private static ImportCandidate? TryClassify(string fullRoot, string relativeFile, IList<string> warnings)
    {
        string xml;
        byte[] bytes;
        XDocument document;
        try
        {
            bytes = File.ReadAllBytes(Path.Combine(fullRoot, relativeFile));
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            xml = reader.ReadToEnd();
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
        var kind = rootElement switch
        {
            "SW.Blocks.OB" or "SW.Blocks.FB" or "SW.Blocks.FC" => ImportKind.ProgramBlock,
            "SW.Blocks.GlobalDB" or "SW.Blocks.InstanceDB" or "SW.Blocks.ArrayDB" or "SW.Blocks.DB" => ImportKind.DataBlock,
            "SW.Types.PlcStruct" => ImportKind.Udt,
            "SW.Tags.PlcTagTable" => ImportKind.TagTable,
            _ => ImportKind.Unsupported,
        };
        if (kind == ImportKind.Unsupported)
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
            kind,
            kind == ImportKind.ProgramBlock ? rootElement.Substring(ProgramBlockPrefix.Length) : null,
            relativeFile.Count(character => character == '/' || character == '\\'),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
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

    private enum ImportKind
    {
        Unsupported,
        ProgramBlock,
        DataBlock,
        Udt,
        TagTable,
    }

    private sealed class ImportCandidate
    {
        public ImportCandidate(
            string relativeFile,
            string xml,
            string name,
            string rootElement,
            ImportKind kind,
            string? category,
            int depth,
            string contentHash)
        {
            RelativeFile = relativeFile;
            Xml = xml;
            Name = name;
            RootElement = rootElement;
            Kind = kind;
            Category = category;
            Depth = depth;
            ContentHash = contentHash;
            Identity = rootElement + "\n" + name;
        }

        public string RelativeFile { get; }

        public string Xml { get; }

        public string Name { get; }

        public string RootElement { get; }

        public ImportKind Kind { get; }

        /// <summary>Block category (OB/FB/FC) for program blocks; null otherwise.</summary>
        public string? Category { get; }

        public int Depth { get; }

        public string ContentHash { get; }

        /// <summary>Duplicate-detection identity: content root element + block name.</summary>
        public string Identity { get; }
    }
}
