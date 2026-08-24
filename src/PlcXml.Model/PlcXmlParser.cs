using System.Collections.ObjectModel;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PlcXml.Model;

public static class PlcXmlParser
{
    public static PlcDocument Parse(ReadOnlyMemory<byte> bytes, string? sourceName = null)
    {
        if (bytes.IsEmpty)
            throw new PlcXmlParseException("PLCXML_PARSE_INVALID", "XML input is empty.", sourceName);
        var original = bytes.ToArray();
        XDocument tree;
        try
        {
            using var stream = new MemoryStream(original, writable: false);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                IgnoreWhitespace = false, IgnoreComments = false, IgnoreProcessingInstructions = false };
            using var reader = XmlReader.Create(stream, settings);
            tree = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            throw new PlcXmlParseException("PLCXML_PARSE_INVALID", "The XML document is malformed or unsafe.", sourceName, ex);
        }
        if (tree.Root is null || tree.Root.Name.LocalName != "Document")
            throw new PlcXmlParseException("PLCXML_ROOT_UNSUPPORTED", "The XML root is not a supported PLC Document.", sourceName);

        var roots = new List<PlcObject>();
        var raw = new List<PlcRawValue>();
        var children = new List<PlcNode>();
        foreach (var child in tree.Root.Elements())
        {
            if (IsRootObjectCandidate(child))
            {
                var item = ParseObject(child, $"/Document/{child.Name.LocalName}[{roots.Count}]");
                roots.Add(item); children.Add(item);
            }
            else
            {
                var value = new PlcRawValue(child);
                raw.Add(value); children.Add(value);
            }
        }
        var hasBom = original.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })
            || original.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })
            || original.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF });
        var encoding = DetectEncoding(original, tree.Declaration?.Encoding);
        return new PlcDocument(original, tree, sourceName, roots.AsReadOnly(), raw.AsReadOnly(), children.AsReadOnly(), encoding.WebName,
            hasBom, DetectCrLf(original));
    }

    private static PlcObject ParseObject(XElement element, string path)
    {
        var attrs = new List<PlcAttribute>(); var compositions = new List<PlcObject>();
        var raw = new List<PlcRawValue>(); var children = new List<PlcNode>();
        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == "AttributeList")
            {
                foreach (var value in child.Elements())
                {
                    var attribute = new PlcAttribute(value.Name.LocalName, value.Value, value.Elements().Any() ? value : null);
                    attrs.Add(attribute); children.Add(attribute);
                }
            }
            else if (child.Name.LocalName == "ObjectList")
            {
                foreach (var nested in child.Elements())
                {
                    if (IsObjectCandidate(nested))
                    {
                        var item = ParseObject(nested, $"{path}/ObjectList/{nested.Name.LocalName}[{compositions.Count}]");
                        compositions.Add(item); children.Add(item);
                    }
                    else { var value = new PlcRawValue(nested); raw.Add(value); children.Add(value); }
                }
            }
            else { var value = new PlcRawValue(child); raw.Add(value); children.Add(value); }
        }
        var xmlAttributes = element.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal);
        xmlAttributes.TryGetValue("ID", out var id);
        return new PlcObject(element.Name.LocalName, id, new PlcLocation(path, id), attrs.AsReadOnly(),
            compositions.AsReadOnly(), raw.AsReadOnly(), new ReadOnlyDictionary<string, string>(xmlAttributes), children.AsReadOnly());
    }

    private static bool IsObjectCandidate(XElement element) => element.Name.LocalName is not ("DocumentInfo" or "Engineering" or "AttributeList" or "ObjectList");
    private static bool IsRootObjectCandidate(XElement element) => IsObjectCandidate(element) && element.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal);
    private static Encoding DetectEncoding(byte[] bytes, string? declarationEncoding)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return Encoding.Unicode;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return Encoding.BigEndianUnicode;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return new UTF8Encoding(true);
        if (!string.IsNullOrWhiteSpace(declarationEncoding))
        {
            try { return Encoding.GetEncoding(declarationEncoding); }
            catch (ArgumentException) { }
        }
        return new UTF8Encoding(false);
    }
    private static bool DetectCrLf(byte[] bytes)
    {
        for (var i = 0; i + 1 < bytes.Length; i++) if (bytes[i] == '\r' && bytes[i + 1] == '\n') return true;
        return false;
    }
}
