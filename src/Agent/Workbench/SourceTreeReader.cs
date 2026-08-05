using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Contracts.Engineering;

namespace Agent.Workbench;

/// <summary>Reads the managed XML source tree while ignoring export-only metadata.</summary>
public sealed class SourceTreeReader
{
    public IReadOnlyList<SourceObjectSnapshot> Read(string root)
    {
        var validatedRoot = WorkbenchPaths.ValidateResolvedRoot(root);
        if (!Directory.Exists(validatedRoot))
            return Array.Empty<SourceObjectSnapshot>();

        var snapshots = new List<SourceObjectSnapshot>();
        var pending = new Stack<string>();
        pending.Push(validatedRoot);

        while (pending.TryPop(out var directory))
        {
            RejectReparsePoint(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).OrderBy(path => path, StringComparer.Ordinal))
            {
                RejectReparsePoint(entry);
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                if (!string.Equals(Path.GetExtension(entry), ".xml", StringComparison.OrdinalIgnoreCase))
                    continue;

                var relativePath = Path.GetRelativePath(validatedRoot, entry).Replace(Path.DirectorySeparatorChar, '/');
                _ = WorkbenchPaths.ResolveRelativeBelowValidatedRoot(validatedRoot, relativePath);
                var xml = File.ReadAllText(entry);
                var identity = Describe(xml, relativePath, out var category, out var name);
                snapshots.Add(new SourceObjectSnapshot(
                    identity,
                    relativePath,
                    category,
                    name,
                    ComputeHexHash(XmlCompare.Normalize(xml)),
                    new FileInfo(entry).Length));
            }
        }

        return snapshots.OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static string Describe(string xml, string relativePath, out string category, out string name)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var document = XDocument.Load(reader);
            var element = document.Root?.Descendants().FirstOrDefault(IsSupportedObject);
            if (element is not null)
            {
                category = CategoryOf(element.Name.LocalName);
                name = AttributeValue(element, "Name")
                    ?? AttributeValue(element.Element(element.Name.Namespace + "AttributeList"), "Name")
                    ?? Path.GetFileNameWithoutExtension(relativePath);
                var id = element.Attribute("ID")?.Value ?? name;
                return $"{category}:{name}:{id}";
            }
        }
        catch (XmlException)
        {
            // The source path and filename still provide a stable fallback identity for a
            // malformed export; a later validation step reports the XML error.
        }

        var segments = relativePath.Split('/');
        category = segments.Length > 1 ? segments[0] : "Unknown";
        name = Path.GetFileNameWithoutExtension(relativePath);
        return $"{category}:{relativePath}";
    }

    private static bool IsSupportedObject(XElement element) =>
        CategoryOf(element.Name.LocalName) != "Unknown";

    private static string CategoryOf(string localName) => localName switch
    {
        "SW.Blocks.OB" or "SW.Blocks.FB" or "SW.Blocks.FC" => "Blocks",
        "SW.Blocks.DB" or "SW.Blocks.GlobalDB" or "SW.Blocks.InstanceDB" or "SW.Blocks.ArrayDB" => "DB",
        "SW.Tags.PlcTagTable" => "Tags",
        "SW.Types.PlcStruct" or "SW.Types.PlcEnum" or "SW.Types.PlcArray" or "SW.Types.PlcType" => "UDT",
        _ => "Unknown",
    };

    private static string? AttributeValue(XElement? owner, string name) =>
        owner?.Attribute(name)?.Value
        ?? owner?.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value;

    private static string ComputeHexHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new WorkbenchPathException($"The source tree traverses reparse point '{path}'.");
    }
}
