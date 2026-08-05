using System.Xml;
using System.Xml.Linq;

namespace Contracts.Engineering;

/// <summary>A semantic summary of the editable and protected portions of a Siemens PLC XML export.</summary>
public sealed record PlcXmlChangeSummary
{
    private static readonly string[] SafeHeaderFields =
        { "HeaderAuthor", "HeaderFamily", "HeaderName" };

    private static readonly HashSet<string> SupportedRootNames = new(StringComparer.Ordinal)
    {
        "SW.Blocks.OB",
        "SW.Blocks.FB",
        "SW.Blocks.FC",
        "SW.Blocks.DB",
        "SW.Blocks.GlobalDB",
        "SW.Blocks.InstanceDB",
        "SW.Blocks.ArrayDB",
        "SW.Types.PlcStruct",
        "SW.Tags.PlcTagTable"
    };

    public PlcXmlChangeSummary(
        bool summaryAvailable,
        bool logicOrStructureChanged,
        IReadOnlyList<PlcXmlHeaderChange> headerChanges,
        IReadOnlyList<PlcXmlMultilingualTextChange> multilingualTextChanges)
    {
        SummaryAvailable = summaryAvailable;
        LogicOrStructureChanged = logicOrStructureChanged;
        HeaderChanges = headerChanges;
        MultilingualTextChanges = multilingualTextChanges;
    }

    public bool SummaryAvailable { get; }
    public bool LogicOrStructureChanged { get; }
    public IReadOnlyList<PlcXmlHeaderChange> HeaderChanges { get; }
    public IReadOnlyList<PlcXmlMultilingualTextChange> MultilingualTextChanges { get; }

    /// <summary>
    /// Compares two Siemens exports. If either document is malformed, unsupported, or
    /// semantically ambiguous, no meaning is inferred and an unavailable summary is returned.
    /// </summary>
    public static PlcXmlChangeSummary Compare(string oldXml, string newXml)
    {
        try
        {
            var before = Parse(oldXml);
            var after = Parse(newXml);
            var beforeRoot = FindSupportedRoot(before);
            var afterRoot = FindSupportedRoot(after);
            var beforeText = TextValues(beforeRoot);
            var afterText = TextValues(afterRoot);
            var baselineStructure = BaselineTextStructure.Create(beforeText);

            return new PlcXmlChangeSummary(
                true,
                !string.Equals(
                    ProtectedCanonical(before, null),
                    ProtectedCanonical(after, baselineStructure),
                    StringComparison.Ordinal),
                CompareHeaders(HeaderValues(beforeRoot), HeaderValues(afterRoot)),
                CompareTextValues(beforeText, afterText));
        }
        catch (Exception exception) when (exception is XmlException
            or InvalidOperationException
            or ArgumentException
            or NullReferenceException)
        {
            return Unavailable();
        }
    }

    private static PlcXmlChangeSummary Unavailable() =>
        new(false, false, Array.Empty<PlcXmlHeaderChange>(), Array.Empty<PlcXmlMultilingualTextChange>());

    private static XDocument Parse(string xml)
    {
        if (xml == null) throw new ArgumentNullException(nameof(xml));
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, settings);
        return XDocument.Load(reader);
    }

    private static XElement FindSupportedRoot(XDocument document)
    {
        var matches = document.Descendants()
            .Where(element => SupportedRootNames.Contains(element.Name.LocalName))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("A Siemens export must contain exactly one supported PLC object.");
        return matches[0];
    }

    private static bool IsBlock(XElement root) => root.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal);

    private static Dictionary<string, string?> HeaderValues(XElement root)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!IsBlock(root)) return result;

        var attributes = DirectChild(root, "AttributeList");
        if (attributes == null) return result;
        foreach (var field in SafeHeaderFields)
            if (DirectChild(attributes, field) is { } element)
                result.Add(field, RequireDirectScalarValue(element));
        return result;
    }

    private static PlcXmlHeaderChange[] CompareHeaders(
        IReadOnlyDictionary<string, string?> before,
        IReadOnlyDictionary<string, string?> after) =>
        before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .OrderBy(field => field, StringComparer.Ordinal)
            .Where(field => !string.Equals(
                before.TryGetValue(field, out var oldValue) ? oldValue : null,
                after.TryGetValue(field, out var newValue) ? newValue : null,
                StringComparison.Ordinal))
            .Select(field => new PlcXmlHeaderChange(
                field,
                before.TryGetValue(field, out var oldValue) ? oldValue : null,
                after.TryGetValue(field, out var newValue) ? newValue : null))
            .ToArray();

    private static Dictionary<string, TextValue> TextValues(XElement root)
    {
        var result = new Dictionary<string, TextValue>(StringComparer.Ordinal);
        foreach (var owner in TextOwners(root))
            AddOwnerTextValues(result, owner);
        return result;
    }

    private static IEnumerable<TextOwner> TextOwners(XElement root)
    {
        yield return new TextOwner(root, RootOwnerKind(root), XmlId(root), null);

        if (IsBlock(root))
        {
            var units = root.Descendants()
                .Where(element => element.Name.LocalName == "SW.Blocks.CompileUnit")
                .ToArray();
            for (var index = 0; index < units.Length; index++)
                yield return new TextOwner(units[index], "network", XmlId(units[index]), index + 1);
        }
        else if (root.Name.LocalName == "SW.Tags.PlcTagTable")
        {
            foreach (var tag in root.Descendants().Where(element => element.Name.LocalName == "SW.Tags.PlcTag"))
                yield return new TextOwner(tag, "tag", XmlId(tag), null);
        }
    }

    private static string RootOwnerKind(XElement root) => root.Name.LocalName switch
    {
        "SW.Types.PlcStruct" => "udt",
        "SW.Tags.PlcTagTable" => "tagTable",
        _ => "block"
    };

    private static void AddOwnerTextValues(IDictionary<string, TextValue> result, TextOwner owner)
    {
        var objectList = DirectChild(owner.Element, "ObjectList");
        if (objectList == null) return;

        foreach (var composition in objectList.Elements()
                     .Where(element => element.Name.LocalName == "MultilingualText"
                         && XmlAttribute(element, "CompositionName") is "Title" or "Comment"))
        {
            var field = XmlAttribute(composition, "CompositionName");
            foreach (var item in composition.Descendants()
                         .Where(element => element.Name.LocalName == "MultilingualTextItem"))
            {
                var attributes = DirectChild(item, "AttributeList");
                var culture = attributes == null ? null : DirectChild(attributes, "Culture");
                var text = attributes == null ? null : DirectChild(attributes, "Text");
                if (culture == null || text == null) continue;

                var value = new TextValue(
                    owner,
                    composition,
                    item,
                    field,
                    culture.Value,
                    RequireDirectScalarValue(text));
                if (result.ContainsKey(value.Key))
                    throw new InvalidOperationException("A multilingual owner/field/culture key is duplicated.");
                result.Add(value.Key, value);
            }
        }
    }

    private static PlcXmlMultilingualTextChange[] CompareTextValues(
        IReadOnlyDictionary<string, TextValue> before,
        IReadOnlyDictionary<string, TextValue> after) =>
        before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Select(key =>
            {
                before.TryGetValue(key, out var oldEntry);
                after.TryGetValue(key, out var newEntry);
                return (Old: oldEntry, New: newEntry, Display: newEntry ?? oldEntry!);
            })
            .Where(pair => !string.Equals(pair.Old?.Value, pair.New?.Value, StringComparison.Ordinal))
            .OrderBy(pair => OwnerRank(pair.Display.Owner.Kind))
            .ThenBy(pair => pair.Display.Owner.Id, StringComparer.Ordinal)
            .ThenBy(pair => pair.Display.Field, StringComparer.Ordinal)
            .ThenBy(pair => pair.Display.Culture, StringComparer.Ordinal)
            .Select(pair => new PlcXmlMultilingualTextChange(
                pair.Display.Owner.Kind,
                pair.Display.Owner.Id,
                pair.Display.Owner.NetworkNumber,
                pair.Display.Field,
                pair.Display.Culture,
                pair.Old?.Value,
                pair.New?.Value))
            .ToArray();

    private static int OwnerRank(string ownerKind) => ownerKind switch
    {
        "block" => 0,
        "udt" => 1,
        "tagTable" => 2,
        "network" => 3,
        "tag" => 4,
        _ => 5
    };

    private static string ProtectedCanonical(XDocument document, BaselineTextStructure? baseline)
    {
        var clone = new XDocument(document);
        RemoveExportCreatedMetadata(clone);
        var root = FindSupportedRoot(clone);

        if (baseline != null)
            RemoveApprovedNewCultureItems(clone, root, baseline);

        MaskEditableTextValues(root);
        MaskSafeHeaderValues(root);
        NormalizeFormattingAndAttributes(clone);
        return clone.ToString(SaveOptions.DisableFormatting).Replace("\r\n", "\n");
    }

    private static void RemoveExportCreatedMetadata(XDocument document)
    {
        var root = document.Root;
        if (root?.Name.LocalName != "Document") return;
        foreach (var documentInfo in root.Elements().Where(element => element.Name.LocalName == "DocumentInfo"))
            foreach (var created in documentInfo.Elements().Where(element => element.Name.LocalName == "Created").ToArray())
                created.Remove();
    }

    private static void RemoveApprovedNewCultureItems(
        XDocument candidate,
        XElement candidateRoot,
        BaselineTextStructure baseline)
    {
        var duplicateIds = new HashSet<string>(candidate.Descendants()
            .SelectMany(element => element.Attributes().Where(attribute => attribute.Name.LocalName == "ID"))
            .GroupBy(attribute => attribute.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key), StringComparer.Ordinal);

        foreach (var value in TextValues(candidateRoot).Values.ToArray())
        {
            if (baseline.ValueKeys.Contains(value.Key)
                || !baseline.CompositionKeys.Contains(value.CompositionKey)
                || duplicateIds.Contains(XmlId(value.Item))
                || !baseline.ApprovedItemShapes.Contains(ItemShape(value.Item)))
                continue;
            value.Item.Remove();
        }
    }

    private static void MaskEditableTextValues(XElement root)
    {
        foreach (var owner in TextOwners(root))
        {
            var objectList = DirectChild(owner.Element, "ObjectList");
            foreach (var composition in objectList?.Elements()
                         .Where(element => element.Name.LocalName == "MultilingualText"
                             && XmlAttribute(element, "CompositionName") is "Title" or "Comment")
                     ?? Enumerable.Empty<XElement>())
            {
                foreach (var item in composition.Descendants()
                             .Where(element => element.Name.LocalName == "MultilingualTextItem"))
                {
                    var attributes = DirectChild(item, "AttributeList");
                    if (attributes != null && DirectChild(attributes, "Text") is { } text)
                        MaskDirectScalarValue(text);
                }
            }
        }
    }

    private static void MaskSafeHeaderValues(XElement root)
    {
        if (!IsBlock(root)) return;
        var attributes = DirectChild(root, "AttributeList");
        if (attributes == null) return;
        foreach (var field in SafeHeaderFields)
            foreach (var element in attributes.Elements().Where(element => element.Name.LocalName == field))
                MaskDirectScalarValue(element);
    }

    private static string ItemShape(XElement item)
    {
        var clone = new XElement(item);
        clone.SetAttributeValue("ID", "__NEW_ID__");
        var attributes = DirectChild(clone, "AttributeList");
        var culture = attributes == null ? null : DirectChild(attributes, "Culture");
        var text = attributes == null ? null : DirectChild(attributes, "Text");
        if (culture == null || text == null) return "__INVALID_ITEM__";
        culture.Value = "__CULTURE__";
                        MaskDirectScalarValue(text);
        NormalizeFormattingAndAttributes(clone);
        return clone.ToString(SaveOptions.DisableFormatting).Replace("\r\n", "\n");
    }

    private static void NormalizeFormattingAndAttributes(XContainer container)
    {
        foreach (var whitespace in container.DescendantNodes().OfType<XText>()
                     .Where(text => text.Parent?.HasElements == true && string.IsNullOrWhiteSpace(text.Value))
                     .ToArray())
            whitespace.Remove();

        var elements = container is XDocument document
            ? document.Descendants()
            : ((XElement)container).DescendantsAndSelf();
        foreach (var element in elements.ToArray())
        {
            var attributes = element.Attributes()
                .OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal)
                .Select(attribute => new XAttribute(attribute))
                .ToArray();
            element.ReplaceAttributes(attributes);
        }
    }

    private static XElement? DirectChild(XElement owner, string localName) =>
        owner.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    private static string RequireDirectScalarValue(XElement element)
    {
        var nodes = element.Nodes().ToArray();
        if (nodes.Length == 0)
            return string.Empty;
        if (nodes.Length != 1 || nodes[0] is not XText value || nodes[0] is XCData)
        {
            throw new InvalidOperationException(
                $"Editable scalar element '{element.Name.LocalName}' must be empty or contain exactly one ordinary direct text node; nested elements, comments, processing instructions, and CDATA are not supported.");
        }
        return value.Value;
    }

    private static void MaskDirectScalarValue(XElement element)
    {
        _ = RequireDirectScalarValue(element);
        element.RemoveNodes();
        element.Add(new XText("__EDITABLE_TEXT__"));
    }

    private static string XmlId(XElement element) => XmlAttribute(element, "ID");

    private static string XmlAttribute(XElement element, string localName) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == localName)?.Value ?? string.Empty;

    private sealed class BaselineTextStructure
    {
        private BaselineTextStructure(
            HashSet<string> valueKeys,
            HashSet<string> compositionKeys,
            HashSet<string> approvedItemShapes)
        {
            ValueKeys = valueKeys;
            CompositionKeys = compositionKeys;
            ApprovedItemShapes = approvedItemShapes;
        }

        public HashSet<string> ValueKeys { get; }
        public HashSet<string> CompositionKeys { get; }
        public HashSet<string> ApprovedItemShapes { get; }

        public static BaselineTextStructure Create(IReadOnlyDictionary<string, TextValue> values) => new(
            new HashSet<string>(values.Keys, StringComparer.Ordinal),
            new HashSet<string>(values.Values.Select(value => value.CompositionKey), StringComparer.Ordinal),
            new HashSet<string>(values.Values.Select(value => ItemShape(value.Item)), StringComparer.Ordinal));
    }

    private sealed class TextOwner
    {
        public TextOwner(XElement element, string kind, string id, int? networkNumber)
        {
            Element = element;
            Kind = kind;
            Id = id;
            NetworkNumber = networkNumber;
        }

        public XElement Element { get; }
        public string Kind { get; }
        public string Id { get; }
        public int? NetworkNumber { get; }
        public string Key => string.Join("\u001f", Kind, Id);
    }

    private sealed class TextValue
    {
        public TextValue(
            TextOwner owner,
            XElement composition,
            XElement item,
            string field,
            string culture,
            string value)
        {
            Owner = owner;
            Composition = composition;
            Item = item;
            Field = field;
            Culture = culture;
            Value = value;
        }

        public TextOwner Owner { get; }
        public XElement Composition { get; }
        public XElement Item { get; }
        public string Field { get; }
        public string Culture { get; }
        public string Value { get; }
        public string CompositionKey => string.Join("\u001f", Owner.Key, Field);
        public string Key => string.Join("\u001f", CompositionKey, Culture);
    }
}

/// <summary>A changed safe Siemens block-header field.</summary>
public sealed record PlcXmlHeaderChange
{
    public PlcXmlHeaderChange(string field, string? oldValue, string? newValue)
    {
        Field = field;
        OldValue = oldValue;
        NewValue = newValue;
    }

    public string Field { get; }
    public string? OldValue { get; }
    public string? NewValue { get; }
}

/// <summary>A changed multilingual Title or Comment value.</summary>
public sealed record PlcXmlMultilingualTextChange
{
    public PlcXmlMultilingualTextChange(
        string ownerKind,
        string ownerId,
        int? networkNumber,
        string field,
        string culture,
        string? oldValue,
        string? newValue)
    {
        OwnerKind = ownerKind;
        OwnerId = ownerId;
        NetworkNumber = networkNumber;
        Field = field;
        Culture = culture;
        OldValue = oldValue;
        NewValue = newValue;
    }

    public string OwnerKind { get; }
    public string OwnerId { get; }
    public int? NetworkNumber { get; }
    public string Field { get; }
    public string Culture { get; }
    public string? OldValue { get; }
    public string? NewValue { get; }
}
