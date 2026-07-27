using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Mcp.Knowledge.Graph;

namespace Mcp.Knowledge.Import;

public sealed class EffectiveSourceImportResult
{
    public EffectiveSourceImportResult(
        SemanticPlcGraph graph,
        int filesFound,
        int filesImported,
        IReadOnlyList<string> warnings)
    {
        Graph = graph;
        FilesFound = filesFound;
        FilesImported = filesImported;
        Warnings = warnings;
    }

    public SemanticPlcGraph Graph { get; }

    public int FilesFound { get; }

    public int FilesImported { get; }

    public IReadOnlyList<string> Warnings { get; }

    public IReadOnlyList<ComponentImport> Components => Graph.ComponentImports;

    public string Source => "effective-manifest";
}

public static class EffectiveSourceImporter
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static EffectiveSourceImportResult Import(
        string exportedSourceRoot,
        string modifiedSourceRoot,
        Action<string>? progress = null)
    {
        var exportedRoot = RequireRoot(exportedSourceRoot, nameof(exportedSourceRoot));
        var modifiedRoot = RequireRoot(modifiedSourceRoot, nameof(modifiedSourceRoot));
        RequireDistinctRoots(exportedRoot, modifiedRoot);
        var document = ReadManifest(exportedRoot);
        var components = document.Components ?? new List<ComponentMetadataRecordDto>();
        var manifestPaths = IndexManifestPaths(components);
        var overlayFiles = EnumerateXmlFiles(modifiedRoot);

        foreach (var overlay in overlayFiles)
        {
            if (manifestPaths.TryGetValue(overlay, out var baseline))
            {
                ValidateBaselineIdentity(
                    ResolvePath(modifiedRoot, overlay),
                    overlay,
                    baseline);
                continue;
            }

            var classified = ClassifyOverlay(ResolvePath(modifiedRoot, overlay), overlay);
            EnsureIdentityIsNew(components, classified, overlay);
            components.Add(CreateOverlayComponent(overlay, classified));
        }

        return WithEffectiveRoot(
            exportedRoot,
            modifiedRoot,
            components,
            effectiveRoot =>
            {
                var imported = ManifestImporter.Import(effectiveRoot, progress);
                PointProjectAtExportedRoot(imported.Graph, exportedSourceRoot);
                return new EffectiveSourceImportResult(
                    imported.Graph,
                    imported.FilesFound,
                    imported.FilesImported,
                    imported.Warnings);
            });
    }

    public static ExportFolderImportResult ImportComponent(
        string exportedSourceRoot,
        string modifiedSourceRoot,
        string relativePath)
    {
        var exportedRoot = RequireRoot(exportedSourceRoot, nameof(exportedSourceRoot));
        var modifiedRoot = RequireRoot(modifiedSourceRoot, nameof(modifiedSourceRoot));
        RequireDistinctRoots(exportedRoot, modifiedRoot);
        var normalizedPath = NormalizeRelativePath(relativePath);
        var overlayPath = ResolvePath(modifiedRoot, normalizedPath);
        if (!File.Exists(overlayPath))
        {
            throw new FileNotFoundException(
                $"Overlay component '{normalizedPath}' was not found under '{modifiedSourceRoot}'.",
                overlayPath);
        }

        var document = ReadManifest(exportedRoot);
        var components = document.Components ?? new List<ComponentMetadataRecordDto>();
        var manifestPaths = IndexManifestPaths(components);
        ComponentMetadataRecordDto component;
        if (manifestPaths.TryGetValue(normalizedPath, out var baseline))
        {
            ValidateBaselineIdentity(overlayPath, normalizedPath, baseline);
            component = baseline;
        }
        else
        {
            var classified = ClassifyOverlay(overlayPath, normalizedPath);
            EnsureIdentityIsNew(components, classified, normalizedPath);
            component = CreateOverlayComponent(normalizedPath, classified);
        }

        return WithEffectiveRoot(
            exportedRoot,
            modifiedRoot,
            new[] { component },
            effectiveRoot =>
            {
                var imported = ManifestImporter.ImportComponent(
                    effectiveRoot,
                    normalizedPath);
                PointProjectAtExportedRoot(imported.Graph, exportedSourceRoot);
                return imported;
            });
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Component path is required.", nameof(relativePath));
        }

        var normalized = relativePath.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length == 0 ||
            Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(segment =>
                segment.Length == 0 ||
                segment == "." ||
                segment == ".."))
        {
            throw new ArgumentException(
                $"Component path '{relativePath}' must be a normalized relative path.",
                nameof(relativePath));
        }

        return normalized;
    }

    public static string ResolvePath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var normalizedPath = NormalizeRelativePath(relativePath);
        var fullPath = Path.GetFullPath(
            Path.Combine(
                fullRoot,
                normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = fullRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(
                rootPrefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Component path '{relativePath}' resolves outside '{root}'.",
                nameof(relativePath));
        }

        RejectExistingReparsePoints(fullPath);
        return fullPath;
    }

    private static T WithEffectiveRoot<T>(
        string exportedRoot,
        string modifiedRoot,
        IEnumerable<ComponentMetadataRecordDto> components,
        Func<string, T> action)
    {
        var tempParent = Path.Combine(
            Path.GetTempPath(),
            "Mcp.Knowledge",
            "effective-source",
            Guid.NewGuid().ToString("N"));
        var rootName = Path.GetFileName(
            exportedRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(rootName))
        {
            rootName = "device";
        }

        var effectiveRoot = Path.Combine(tempParent, rootName);
        Directory.CreateDirectory(effectiveRoot);
        try
        {
            var materialized = components.ToList();
            foreach (var component in materialized)
            {
                if (string.IsNullOrWhiteSpace(component.ExportedFile))
                {
                    continue;
                }

                var relativePath = NormalizeRelativePath(component.ExportedFile);
                component.ExportedFile = relativePath;
                var overlayPath = ResolvePath(modifiedRoot, relativePath);
                var baselinePath = ResolvePath(exportedRoot, relativePath);
                var sourcePath = File.Exists(overlayPath)
                    ? overlayPath
                    : baselinePath;
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                var targetPath = ResolvePath(effectiveRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(sourcePath, targetPath, overwrite: true);
            }

            var effectiveManifest = new ComponentMetadataDocumentDto
            {
                SchemaVersion = ManifestImporter.ExpectedSchemaVersion,
                Components = materialized,
            };
            File.WriteAllText(
                Path.Combine(effectiveRoot, ManifestImporter.MetadataFileName),
                JsonSerializer.Serialize(effectiveManifest, JsonOptions));
            return action(effectiveRoot);
        }
        finally
        {
            try
            {
                Directory.Delete(tempParent, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; failed temp cleanup must not mask an import result.
            }
        }
    }

    private static string RequireRoot(string root, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Source root is required.", parameterName);
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Source root '{root}' was not found.");
        }

        var fullRoot = Path.GetFullPath(root);
        RejectExistingReparsePoints(fullRoot);
        return fullRoot;
    }

    private static ComponentMetadataDocumentDto ReadManifest(string exportedRoot)
    {
        var manifestPath = Path.Combine(
            exportedRoot,
            ManifestImporter.MetadataFileName);
        if (!File.Exists(manifestPath))
        {
            throw new ManifestInvalidException(
                $"metadata.json was not found under exported source root '{exportedRoot}'.");
        }

        try
        {
            return JsonSerializer.Deserialize<ComponentMetadataDocumentDto>(
                    File.ReadAllText(manifestPath),
                    JsonOptions)
                ?? new ComponentMetadataDocumentDto();
        }
        catch (JsonException ex)
        {
            throw new ManifestInvalidException(
                $"metadata.json in '{exportedRoot}' is not valid JSON: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new ManifestInvalidException(
                $"metadata.json in '{exportedRoot}' could not be read: {ex.Message}");
        }
    }

    private static Dictionary<string, ComponentMetadataRecordDto> IndexManifestPaths(
        IEnumerable<ComponentMetadataRecordDto> components)
    {
        var paths = new Dictionary<string, ComponentMetadataRecordDto>(PathComparer);
        foreach (var component in components)
        {
            if (string.IsNullOrWhiteSpace(component.ExportedFile))
            {
                continue;
            }

            var normalizedPath = NormalizeRelativePath(component.ExportedFile);
            if (!paths.TryAdd(normalizedPath, component))
            {
                throw new ManifestInvalidException(
                    $"metadata.json contains duplicate component path '{normalizedPath}'.");
            }
        }

        return paths;
    }

    private static string[] EnumerateXmlFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push((DirectoryInfo)entry);
                    continue;
                }

                if (string.Equals(
                        entry.Extension,
                        ".xml",
                        StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(NormalizeRelativePath(
                        Path.GetRelativePath(root, entry.FullName)));
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files.ToArray();
    }

    private static void RequireDistinctRoots(
        string exportedRoot,
        string modifiedRoot)
    {
        if (string.Equals(
                exportedRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                modifiedRoot.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ManifestInvalidException(
                "exportedSourceRoot and modifiedSourceRoot must resolve to different directories.");
        }
    }

    private static void RejectExistingReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(pathRoot))
        {
            throw new ManifestInvalidException(
                $"Source path '{path}' has no filesystem root.");
        }

        var current = pathRoot;
        foreach (var segment in fullPath[pathRoot.Length..].Split(
                     new[]
                     {
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar,
                     },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
            catch (Exception ex) when (
                ex is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                throw new ManifestInvalidException(
                    $"Source path segment '{current}' could not be validated: {ex.Message}");
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ManifestInvalidException(
                    $"Source path '{path}' traverses reparse point '{current}'.");
            }
        }
    }

    private static void ValidateBaselineIdentity(
        string overlayPath,
        string relativePath,
        ComponentMetadataRecordDto baseline)
    {
        var classified = ClassifyOverlay(overlayPath, relativePath);
        if (!string.Equals(classified.Name, baseline.Name, StringComparison.Ordinal) ||
            !string.Equals(
                classified.Category,
                baseline.Category,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ComponentIdentityMismatchException(
                $"Overlay component '{relativePath}' declares '{classified.Name}' ({classified.Category}), " +
                $"but metadata.json identifies '{baseline.Name ?? "(missing)"}' ({baseline.Category ?? "(missing)"}).");
        }
    }

    private static void EnsureIdentityIsNew(
        IEnumerable<ComponentMetadataRecordDto> components,
        ClassifiedComponent classified,
        string relativePath)
    {
        var existing = components.FirstOrDefault(component =>
            string.Equals(component.Name, classified.Name, StringComparison.Ordinal) &&
            string.Equals(
                component.Category,
                classified.Category,
                StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            throw new ComponentIdentityMismatchException(
                $"Overlay-only component '{relativePath}' declares '{classified.Name}' ({classified.Category}), " +
                $"which metadata.json already assigns to '{existing.ExportedFile ?? "(missing path)"}'.");
        }
    }

    private static ComponentMetadataRecordDto CreateOverlayComponent(
        string relativePath,
        ClassifiedComponent classified)
    {
        var sourcePath = Path.ChangeExtension(relativePath, null)?
            .Replace('\\', '/') ?? relativePath;
        return new ComponentMetadataRecordDto
        {
            Name = classified.Name,
            SourcePath = sourcePath,
            Category = classified.Category,
            Status = "Exported",
            ExportedFile = relativePath,
        };
    }

    private static ClassifiedComponent ClassifyOverlay(
        string path,
        string relativePath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (XmlException ex)
        {
            throw new ManifestInvalidException(
                $"overlay component '{relativePath}' contains malformed XML: {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new ManifestInvalidException(
                $"overlay component '{relativePath}' could not be read: {ex.Message}");
        }

        var contentRoot = document.Root;
        if (contentRoot != null &&
            !contentRoot.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal))
        {
            contentRoot = contentRoot
                .Elements()
                .FirstOrDefault(element =>
                    element.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal));
        }

        var rootElement = contentRoot?.Name.LocalName;
        var category = rootElement switch
        {
            "SW.Blocks.OB" => "OB",
            "SW.Blocks.FB" => "FB",
            "SW.Blocks.FC" => "FC",
            "SW.Blocks.GlobalDB" or
            "SW.Blocks.InstanceDB" or
            "SW.Blocks.ArrayDB" or
            "SW.Blocks.DB" => "DB",
            "SW.Types.PlcStruct" => "UDT",
            "SW.Tags.PlcTagTable" => "Tags",
            _ => null,
        };
        if (category == null)
        {
            throw new ManifestInvalidException(
                $"overlay component '{relativePath}' has unsupported root element '{rootElement ?? "(missing)"}'.");
        }

        var name = contentRoot?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "AttributeList")?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Name")?
            .Value
            .Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ManifestInvalidException(
                $"overlay component '{relativePath}' has no component identity (<Name>).");
        }

        return new ClassifiedComponent(name, category);
    }

    private static void PointProjectAtExportedRoot(
        SemanticPlcGraph graph,
        string exportedSourceRoot)
    {
        var project = graph.FindNodesByKind(SemanticNodeKind.Project).SingleOrDefault();
        if (project == null)
        {
            return;
        }

        var properties = new Dictionary<string, string>(
            project.Properties,
            StringComparer.Ordinal)
        {
            ["exportRoot"] = exportedSourceRoot,
        };
        graph.UpsertNode(new SemanticGraphNode(
            project.Id,
            project.Kind,
            project.Name,
            properties));
    }

    private sealed record ClassifiedComponent(string Name, string Category);
}
