using System.Xml.Linq;
using System.Text;
using System.Xml;

namespace PlcXml.Model;

public sealed class PlcDocument
{
    internal PlcDocument(byte[] originalBytes, XDocument originalTree, string? sourceName, IReadOnlyList<PlcObject> objects,
        IReadOnlyList<PlcRawValue> rawValues, IReadOnlyList<PlcNode> children, string encodingName, bool hasBom, bool usesCrLf)
    {
        OriginalBytes = (byte[])originalBytes.Clone();
        _originalTree = originalTree;
        SourceName = sourceName; Objects = objects; RawValues = rawValues; Children = children;
        EncodingName = encodingName; HasBom = hasBom; UsesCrLf = usesCrLf;
        var networkObjects = objects.SelectMany(FindNetworks).ToList();
        _networks = networkObjects.Select((o, i) => new PlcNetwork(this, o,
            networkObjects.Take(i).Count(previous => previous.Id == o.Id))).ToList().AsReadOnly();
    }
    private readonly byte[] OriginalBytes;
    private readonly XDocument _originalTree;
    private readonly IReadOnlyList<PlcNetwork> _networks;
    private readonly List<TextMutation> _mutations = new();
    public string? SourceName { get; }
    public IReadOnlyList<PlcObject> Objects { get; }
    public IReadOnlyList<PlcRawValue> RawValues { get; }
    public IReadOnlyList<PlcNode> Children { get; }
    public string EncodingName { get; }
    public bool HasBom { get; }
    public bool UsesCrLf { get; }
    public byte[] SerializeOriginal() => (byte[])OriginalBytes.Clone();
    public IReadOnlyList<PlcNetwork> Networks => _networks;

    internal void QueueMutation(PlcNetwork network, string field, string culture, string text)
    {
        if (network is null || string.IsNullOrWhiteSpace(field) || (field != "Title" && field != "Comment") ||
            string.IsNullOrWhiteSpace(culture) || text is null)
            throw new PlcXmlModelException("PLCXML_MUTATION_INVALID", "Mutation arguments are invalid.", SourceName, location: network?.Location);
        XElement target;
        XElement composition;
        try
        {
            target = FindNetwork(_originalTree, network.Id, network.Occurrence, network.Location.Path);
            composition = FindCompositions(target, field, network.Location.Path);
        }
        catch (PlcXmlModelException ex) when (ex.SourceName is null || ex.Location is null)
        {
            throw new PlcXmlModelException(ex.Code, ex.Message, SourceName, ex, network.Location);
        }
        var items = FindCultureItems(composition, culture);
        if (items.Count == 0)
            throw new PlcXmlModelException("PLCXML_TEXT_TARGET_NOT_FOUND", $"No {field} text exists for culture '{culture}'.", SourceName, location: network.Location);
        if (items.Count > 1)
            throw new PlcXmlModelException("PLCXML_TEXT_TARGET_AMBIGUOUS", $"More than one {field} text exists for culture '{culture}'.", SourceName, location: network.Location);
        var key = new MutationKey(network.Id!, network.Occurrence, field, culture, network.Location.Path);
        if (_mutations.Any(m => m.Key.Equals(key)))
            throw new PlcXmlModelException("PLCXML_TEXT_TARGET_AMBIGUOUS", "The same text target was already mutated.", SourceName, location: network.Location);
        if (items[0].Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList")?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "Text") is null)
            throw new PlcXmlModelException("PLCXML_TEXT_TARGET_NOT_FOUND", "The text target has no direct Text element.", SourceName, location: network.Location);
        _mutations.Add(new TextMutation(key, text));
    }

    public byte[] SerializeMutated()
    {
        if (_mutations.Count == 0)
            throw new PlcXmlModelException("PLCXML_SERIALIZE_FAILED", "No mutation is pending.", SourceName);
        try
        {
            var clone = new XDocument(_originalTree);
            foreach (var mutation in _mutations)
            {
                var network = FindNetwork(clone, mutation.Key.NetworkId, mutation.Key.Occurrence, mutation.Key.ModelPath);
                var composition = FindCompositions(network, mutation.Key.Field, mutation.Key.ModelPath);
                var item = FindCultureItems(composition, mutation.Key.Culture).Single();
                var text = item.Elements().First(e => e.Name.LocalName == "AttributeList").Elements()
                    .First(e => e.Name.LocalName == "Text");
                text.Value = mutation.Text;
            }
            if (UsesCrLf)
            {
                foreach (var textNode in clone.DescendantNodes().OfType<XText>())
                    textNode.Value = textNode.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
            }
            var encoding = CreateEncoding(EncodingName, HasBom);
            using var stream = new MemoryStream();
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
            {
                Encoding = encoding,
                OmitXmlDeclaration = false,
                Indent = false,
                NewLineHandling = NewLineHandling.None,
                CloseOutput = false
            }))
                clone.Save(writer);
            return stream.ToArray();
        }
        catch (PlcXmlModelException) { throw; }
        catch (Exception ex)
        {
            throw new PlcXmlModelException("PLCXML_SERIALIZE_FAILED", "The mutated XML could not be serialized.", SourceName, ex);
        }
    }

    private static IEnumerable<PlcObject> FindNetworks(PlcObject value) =>
        value.ElementName == "SW.Blocks.CompileUnit"
            ? new[] { value }
            : value.Compositions.SelectMany(FindNetworks);

    private static XElement FindNetwork(XDocument tree, string? id, int occurrence, string modelPath)
    {
        var matches = tree.Descendants().Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit" &&
            (string?)e.Attribute("ID") == id).ToList();
        if (matches.Count <= occurrence)
            throw new PlcXmlModelException("PLCXML_TEXT_TARGET_NOT_FOUND", $"Network '{id}' at {modelPath} was not found.");
        return matches[occurrence];
    }

    private static XElement FindCompositions(XElement network, string field, string modelPath)
    {
        var list = network.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
        var matches = list?.Elements().Where(e => e.Name.LocalName == "MultilingualText" &&
            (string?)e.Attribute("CompositionName") == field).ToList() ?? new List<XElement>();
        if (matches.Count == 0)
            throw new PlcXmlModelException("PLCXML_TEXT_TARGET_NOT_FOUND", $"Network at {modelPath} has no {field} composition.");
        if (matches.Count > 1)
            throw new PlcXmlModelException("PLCXML_TEXT_TARGET_AMBIGUOUS", $"Network at {modelPath} has multiple {field} compositions.");
        return matches[0];
    }

    private static List<XElement> FindCultureItems(XElement composition, string culture)
    {
        var list = composition.Elements().FirstOrDefault(e => e.Name.LocalName == "ObjectList");
        return list?.Elements().Where(e => e.Name.LocalName == "MultilingualTextItem")
            .Where(e => e.Elements().FirstOrDefault(a => a.Name.LocalName == "AttributeList")?.Elements()
                .FirstOrDefault(a => a.Name.LocalName == "Culture")?.Value == culture).ToList() ?? new List<XElement>();
    }

    private static Encoding CreateEncoding(string name, bool bom)
    {
        if (name.Equals("utf-8", StringComparison.OrdinalIgnoreCase)) return new UTF8Encoding(bom);
        if (name.Equals("utf-16", StringComparison.OrdinalIgnoreCase)) return new UnicodeEncoding(false, bom);
        if (name.Equals("utf-16BE", StringComparison.OrdinalIgnoreCase)) return new UnicodeEncoding(true, bom);
        try { return Encoding.GetEncoding(name); } catch (ArgumentException) { return new UTF8Encoding(bom); }
    }

    private readonly record struct MutationKey(string NetworkId, int Occurrence, string Field, string Culture, string ModelPath);
    private sealed record TextMutation(MutationKey Key, string Text);
}
