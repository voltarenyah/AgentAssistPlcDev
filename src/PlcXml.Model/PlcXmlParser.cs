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
                    attribute.Payload = ParsePayload(value);
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

    private static PlcTypedPayload? ParsePayload(XElement value)
    {
        // Interface is intentionally an un-namespaced wrapper. Qualification comes
        // only from its one direct, exact Interface/v5 Sections child.
        if (value.Name == "Interface")
        {
            var sections = value.Elements().Where(e => e.Name == InterfaceSections).ToList();
            if (value.Elements().Count() == 1 && sections.Count == 1)
                return ParseInterface(sections[0]);
            return null;
        }

        // The network source wrapper is the usual envelope, while accepting the
        // qualified payload itself keeps dispatch exact for generic direct values.
        if (value.Name == FlgNetV4) return ParseLadder(value);
        if (value.Name == StructuredTextV3) return ParseStructuredText(value);

        if (value.Name.LocalName != "NetworkSource") return null;
        var payload = value.Elements().SingleOrDefault(e => e.Name == FlgNetV4 || e.Name == StructuredTextV3);
        if (value.Elements().Count() != 1 || payload is null) return null;
        return payload.Name == FlgNetV4 ? ParseLadder(payload) : ParseStructuredText(payload);
    }

    private static readonly XNamespace InterfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
    private static readonly XNamespace NetworkNs = "http://www.siemens.com/automation/Openness/SW/NetworkSource";
    private static readonly XName InterfaceSections = InterfaceNs + "Sections";
    private static readonly XName FlgNetV4 = XNamespace.Get("http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4") + "FlgNet";
    private static readonly XName StructuredTextV3 = XNamespace.Get("http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v3") + "StructuredText";

    private static PlcInterface ParseInterface(XElement sections)
    {
        var typed = new List<PlcInterfaceSection>(); var raw = new List<PlcRawValue>();
        foreach (var child in sections.Elements())
        {
            if (child.Name.LocalName != "Section") { raw.Add(new PlcRawValue(child)); continue; }
            var members = new List<PlcInterfaceMember>(); var sectionRaw = new List<PlcRawValue>();
            foreach (var member in child.Elements())
            {
                if (member.Name.LocalName == "Member")
                {
                    var comment = member.Elements().FirstOrDefault(e => e.Name.LocalName == "Comment")?.Value;
                    members.Add(new PlcInterfaceMember(AttributesOf(member), member.Elements()
                        .Where(e => e.Name.LocalName != "Comment" && e.Name.LocalName != "AttributeList")
                        .Select(e => new PlcRawValue(e)), comment));
                }
                else sectionRaw.Add(new PlcRawValue(member));
            }
            typed.Add(new PlcInterfaceSection((string?)child.Attribute("Name") ?? string.Empty, AttributesOf(child), members, sectionRaw));
        }
        return new PlcInterface(typed, raw);
    }

    private static LadderNetwork ParseLadder(XElement flgNet)
    {
        var accesses = new List<LadderAccess>(); var parts = new List<LadderPart>(); var calls = new List<LadderCall>();
        var wires = new List<LadderWire>(); var raw = new List<RawFlgNode>();
        foreach (var child in flgNet.Elements())
        {
            if (child.Name.LocalName == "Parts")
            {
                foreach (var node in child.Elements())
                {
                    switch (node.Name.LocalName)
                    {
                        case "Access": accesses.Add(new LadderAccess(node)); break;
                        case "Part": parts.Add(new LadderPart(node)); break;
                        case "Call": calls.Add(new LadderCall(node)); break;
                        default: raw.Add(new RawFlgNode(node)); break;
                    }
                }
            }
            else if (child.Name.LocalName == "Wires")
            {
                foreach (var node in child.Elements())
                    if (node.Name.LocalName is "Powerrail" or "NameCon" or "IdentCon" or "OpenCon") wires.Add(new LadderWire(node));
                    else raw.Add(new RawFlgNode(node));
            }
            else raw.Add(new RawFlgNode(child));
        }
        return new LadderNetwork(accesses, parts, calls, wires, raw);
    }

    private static StructuredTextNetwork ParseStructuredText(XElement structuredText)
    {
        var entries = new List<StEntry>();
        foreach (var child in structuredText.Elements())
            entries.Add(child.Name.LocalName switch
            {
                "Token" => new StToken(child), "Blank" => new StBlank(child), "NewLine" => new StNewLine(child),
                "Access" => new StAccess(child), _ => new StRaw(child)
            });
        return new StructuredTextNetwork(entries);
    }

    private static IReadOnlyDictionary<string, string> AttributesOf(XElement element) =>
        new ReadOnlyDictionary<string, string>(element.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value));

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
