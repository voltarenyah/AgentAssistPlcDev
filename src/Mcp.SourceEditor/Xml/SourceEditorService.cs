using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Contracts.Sandbox;
using Mcp.SourceEditor.Models;

namespace Mcp.SourceEditor.Xml;

public sealed class SourceEditorService
{
    private readonly PathJail jail;
    public SourceEditorService(PathJail pathJail) => jail = pathJail;

    public SourceInspection Parse(string xmlFilePath)
    {
        var source = Load(xmlFilePath, nameof(xmlFilePath));
        var block = FindBlock(source.Document);
        var attributes = Child(block, "AttributeList");
        var units = CompileUnits(block);
        return new SourceInspection(
            Value(attributes, "Name") ?? "",
            block.Name.LocalName.Split('.').Last(),
            int.TryParse(Value(attributes, "Number"), out var number) ? number : null,
            Value(attributes, "ProgrammingLanguage"),
            Attr(block, "ID"),
            units.Select((unit, index) => new NetworkInspection(index + 1, Attr(unit, "ID"),
                TextValues(unit, "Title"), TextValues(unit, "Comment"))).ToArray(),
            new[] { "blockHeaderAuthor", "blockHeaderFamily", "blockHeaderName" },
            HashFile(source.Path),
            source.Path);
    }

    public EditBatchResult Apply(string xmlFilePath, IReadOnlyList<SourceEdit> edits,
        string? outputFilePath, bool overwriteOutput, bool inPlace, bool confirmInPlace)
    {
        if (inPlace && !confirmInPlace)
            throw new SourceEditorException("SOURCE_IN_PLACE_CONFIRMATION_REQUIRED",
                "In-place replacement requires confirmInPlace=true.");
        if (inPlace && outputFilePath != null && !SamePath(xmlFilePath, outputFilePath))
            throw new SourceEditorException("SOURCE_OUTPUT_INVALID", "In-place replacement cannot target a different output path.");
        return WriteEdited(xmlFilePath, edits,
            inPlace ? xmlFilePath : outputFilePath ?? DefaultOutput(xmlFilePath, ".edited"),
            overwriteOutput, inPlace);
    }

    public SourceValidationResult Validate(string xmlFilePath, string? baselineFilePath = null)
    {
        var candidate = Load(xmlFilePath, nameof(xmlFilePath));
        var findings = StandaloneFindings(candidate.Document).ToList();
        var protectedMatches = true;
        if (baselineFilePath != null)
        {
            var baseline = Load(baselineFilePath, nameof(baselineFilePath));
            protectedMatches = ProtectedCanonical(baseline.Document) == ProtectedCanonical(candidate.Document)
                && ExistingEditableStructureMatches(baseline.Document, candidate.Document);
            if (!protectedMatches)
                findings.Add(new("error", "SOURCE_INTEGRITY_CHANGED", "Protected PLC logic or structure changed."));
        }
        return new(!findings.Any(f => f.Severity == "error"), protectedMatches, findings);
    }

    public SourceDiffResult Diff(string originalFilePath, string modifiedFilePath)
    {
        var original = Load(originalFilePath, nameof(originalFilePath));
        var modified = Load(modifiedFilePath, nameof(modifiedFilePath));
        var originalFields = EditableFields(original.Document);
        var modifiedFields = EditableFields(modified.Document);
        var changes = originalFields.Keys.Union(modifiedFields.Keys).OrderBy(x => x, StringComparer.Ordinal)
            .Where(key => originalFields.GetValueOrDefault(key, "") != modifiedFields.GetValueOrDefault(key, ""))
            .Select(key =>
            {
                var parts = key.Split('|');
                return new EditableFieldChange(parts[0], parts[1],
                    int.TryParse(parts[2], out var n) ? n : null, parts[3],
                    parts[4].Length == 0 ? null : parts[4],
                    originalFields.GetValueOrDefault(key, ""), modifiedFields.GetValueOrDefault(key, ""));
            }).ToArray();
        var validation = Validate(modifiedFilePath, originalFilePath);
        return new(validation.ProtectedContentMatches, changes, validation.Findings,
            HashFile(original.Path), HashFile(modified.Path));
    }

    private EditBatchResult WriteEdited(string xmlFilePath, IReadOnlyList<SourceEdit> edits,
        string outputFilePath, bool overwrite, bool inPlace)
    {
        if (edits.Count == 0)
            throw new SourceEditorException("SOURCE_OPERATION_UNSUPPORTED", "At least one edit is required.");
        var source = Load(xmlFilePath, nameof(xmlFilePath));
        var sourceHash = HashFile(source.Path);
        var candidate = new XDocument(source.Document);
        var normalized = ApplyEdits(candidate, edits);
        var validation = ValidateDocuments(source.Document, candidate);
        if (!validation.IsValid)
            throw new SourceEditorException("SOURCE_INTEGRITY_CHANGED", "The edit changed protected PLC content.");
        var output = jail.Validate(outputFilePath, nameof(outputFilePath));
        if (!output.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw new SourceEditorException("SOURCE_OUTPUT_INVALID", "Output path must end in .xml.");
        if (!inPlace && File.Exists(output) && !overwrite)
            throw new SourceEditorException("SOURCE_OUTPUT_EXISTS", $"Output file already exists: {output}");
        var temp = WriteTemporary(candidate, output, source.Encoding);
        LoadedXml reopened;
        try
        {
            reopened = Load(temp, nameof(outputFilePath));
            var tempValidation = ValidateDocuments(source.Document, reopened.Document);
            if (!tempValidation.IsValid)
                throw new SourceEditorException("SOURCE_INTEGRITY_CHANGED", "Serialized output changed protected PLC content.");
            File.Move(temp, output, inPlace || overwrite);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
        reopened = Load(output, nameof(outputFilePath));
        var reopenedValidation = ValidateDocuments(source.Document, reopened.Document);
        if (!reopenedValidation.IsValid)
            throw new SourceEditorException("SOURCE_INTEGRITY_CHANGED", "Serialized output changed protected PLC content.");
        return new(source.Path, output, normalized, reopenedValidation, true, sourceHash, HashFile(output));
    }

    private IReadOnlyList<NormalizedEdit> ApplyEdits(XDocument document, IReadOnlyList<SourceEdit> edits)
    {
        var block = FindBlock(document);
        var units = CompileUnits(block);
        var results = new List<NormalizedEdit>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < edits.Count; index++)
        {
            var edit = edits[index];
            XElement owner;
            int? networkNumber = null;
            string field;
            switch (edit.Operation)
            {
                case SourceEditOperation.SetNetworkTitle:
                case SourceEditOperation.SetNetworkComment:
                    (owner, networkNumber) = ResolveNetwork(units, edit.Target, index);
                    field = edit.Operation == SourceEditOperation.SetNetworkTitle ? "Title" : "Comment";
                    break;
                case SourceEditOperation.SetBlockTitle:
                case SourceEditOperation.SetBlockComment:
                    owner = block;
                    field = edit.Operation == SourceEditOperation.SetBlockTitle ? "Title" : "Comment";
                    break;
                case SourceEditOperation.SetSafeProperty:
                    owner = block;
                    field = SetSafeProperty(block, edit.PropertyName, edit.Value, index, out var oldProperty);
                    if (!keys.Add($"{Attr(block, "ID")}|{field}"))
                        throw Error("SOURCE_OPERATION_UNSUPPORTED", "A batch cannot edit the same safe property twice.", index);
                    results.Add(new(index, "block", Attr(block, "ID"), null, field, null, oldProperty, edit.Value));
                    continue;
                default:
                    throw Error("SOURCE_OPERATION_UNSUPPORTED", $"Unsupported operation {edit.Operation}.", index);
            }
            var culture = NormalizeCulture(edit.Culture, index);
            var key = $"{Attr(owner, "ID")}|{field}|{culture}";
            if (!keys.Add(key))
                throw Error("SOURCE_OPERATION_UNSUPPORTED", "A batch cannot edit the same field and culture twice.", index);
            var oldValue = SetMultilingualText(document, owner, field, culture, edit.Value, index, out var actualCulture);
            results.Add(new(index, ReferenceEquals(owner, block) ? "block" : "network", Attr(owner, "ID"),
                networkNumber, field, actualCulture, oldValue, edit.Value));
        }
        return results;
    }

    private static (XElement Unit, int Number) ResolveNetwork(IReadOnlyList<XElement> units, EditTarget? target, int index)
    {
        if (target == null || (target.XmlId == null && target.NetworkNumber == null))
            throw Error("SOURCE_TARGET_NOT_FOUND", "A network target requires xmlId or networkNumber.", index);
        XElement? byId = null;
        XElement? byNumber = null;
        if (target.XmlId != null)
        {
            var matches = units.Where(x => Attr(x, "ID") == target.XmlId).ToArray();
            if (matches.Length > 1) throw Error("SOURCE_TARGET_AMBIGUOUS", $"XML ID {target.XmlId} is ambiguous.", index);
            byId = matches.SingleOrDefault() ?? throw Error("SOURCE_TARGET_NOT_FOUND", $"XML ID {target.XmlId} was not found.", index);
        }
        if (target.NetworkNumber != null)
        {
            if (target.NetworkNumber < 1 || target.NetworkNumber > units.Count)
                throw Error("SOURCE_TARGET_NOT_FOUND", $"Network number {target.NetworkNumber} was not found.", index);
            byNumber = units[target.NetworkNumber.Value - 1];
        }
        if (byId != null && byNumber != null && !ReferenceEquals(byId, byNumber))
            throw Error("SOURCE_TARGET_MISMATCH", "xmlId and networkNumber identify different networks.", index);
        var unit = byId ?? byNumber!;
        return (unit, units.Select((candidate, i) => (candidate, i))
            .First(pair => ReferenceEquals(pair.candidate, unit)).i + 1);
    }

    private static string SetMultilingualText(XDocument document, XElement owner, string field,
        string? requestedCulture, string value, int index, out string actualCulture)
    {
        var composition = owner.Elements().FirstOrDefault(x => x.Name.LocalName == "ObjectList")?
            .Elements().FirstOrDefault(x => x.Name.LocalName == "MultilingualText" && Attr(x, "CompositionName") == field);
        if (composition == null)
        {
            var allElements = document.Descendants().ToList();
            var ownerIndex = allElements.IndexOf(owner);
            var template = allElements.Where(x => x.Name.LocalName == "MultilingualText"
                    && Attr(x, "CompositionName") == field)
                .OrderBy(x => Math.Abs(allElements.IndexOf(x) - ownerIndex))
                .FirstOrDefault()
                ?? throw Error("SOURCE_TEMPLATE_MISSING", $"No {field} composition template exists.", index);
            composition = new XElement(template);
            composition.SetAttributeValue("ID", NextId(document));
            var targetList = owner.Elements().FirstOrDefault(x => x.Name.LocalName == "ObjectList")
                ?? throw Error("SOURCE_TEMPLATE_MISSING", $"Target {Attr(owner, "ID")} has no ObjectList.", index);
            targetList.Add(composition);
            var clonedItems = composition.Descendants().Where(x => x.Name.LocalName == "MultilingualTextItem").ToList();
            foreach (var extra in clonedItems.Skip(1)) extra.Remove();
            if (clonedItems.FirstOrDefault() is { } clonedItem)
            {
                clonedItem.SetAttributeValue("ID", NextId(document));
                var clonedAttributes = Child(clonedItem, "AttributeList");
                SetValue(clonedAttributes, "Culture", requestedCulture ?? "en-US");
                SetValue(clonedAttributes, "Text", "");
            }
        }
        var objectList = composition.Elements().First(x => x.Name.LocalName == "ObjectList");
        var items = objectList.Elements().Where(x => x.Name.LocalName == "MultilingualTextItem").ToList();
        XElement? item = requestedCulture == null ? items.FirstOrDefault() : items.FirstOrDefault(x => Value(Child(x, "AttributeList"), "Culture") == requestedCulture);
        actualCulture = requestedCulture ?? (item == null ? "en-US" : Value(Child(item, "AttributeList"), "Culture") ?? "en-US");
        if (item == null)
        {
            var template = items.FirstOrDefault();
            if (template == null)
            {
                var allElements = document.Descendants().ToList();
                var ownerIndex = allElements.IndexOf(owner);
                template = allElements.Where(x => x.Name.LocalName == "MultilingualText"
                        && Attr(x, "CompositionName") == field)
                    .OrderBy(x => Math.Abs(allElements.IndexOf(x) - ownerIndex))
                    .SelectMany(x => x.Descendants().Where(y => y.Name.LocalName == "MultilingualTextItem"))
                    .FirstOrDefault();
            }
            if (template == null) throw Error("SOURCE_TEMPLATE_MISSING", "No multilingual item template exists.", index);
            item = new XElement(template);
            item.SetAttributeValue("ID", NextId(document));
            var attributes = Child(item, "AttributeList");
            SetValue(attributes, "Culture", actualCulture);
            SetValue(attributes, "Text", "");
            objectList.Add(item);
        }
        var list = Child(item, "AttributeList");
        var oldValue = Value(list, "Text") ?? "";
        SetValue(list, "Text", value);
        return oldValue;
    }

    private static string SetSafeProperty(XElement block, string? propertyName, string value, int index, out string oldValue)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["blockHeaderAuthor"] = "HeaderAuthor",
            ["blockHeaderFamily"] = "HeaderFamily",
            ["blockHeaderName"] = "HeaderName",
        };
        if (propertyName == null || !names.TryGetValue(propertyName, out var xmlName))
            throw Error("SOURCE_PROPERTY_UNSUPPORTED", $"Safe property '{propertyName}' is not supported.", index);
        var attributes = Child(block, "AttributeList");
        var element = attributes.Elements().FirstOrDefault(x => x.Name.LocalName == xmlName)
            ?? throw Error("SOURCE_PROPERTY_UNSUPPORTED", $"Safe property '{propertyName}' is absent from this block schema.", index);
        oldValue = element.Value;
        element.Value = value;
        return propertyName;
    }

    private static SourceValidationResult ValidateDocuments(XDocument baseline, XDocument candidate)
    {
        var findings = StandaloneFindings(candidate).ToList();
        var matches = ProtectedCanonical(baseline) == ProtectedCanonical(candidate)
            && ExistingEditableStructureMatches(baseline, candidate);
        if (!matches) findings.Add(new("error", "SOURCE_INTEGRITY_CHANGED", "Protected PLC logic or structure changed."));
        return new(!findings.Any(x => x.Severity == "error"), matches, findings);
    }

    private static IEnumerable<ValidationFinding> StandaloneFindings(XDocument document)
    {
        _ = FindBlock(document);
        var duplicate = document.Descendants().Attributes("ID").GroupBy(x => x.Value, StringComparer.Ordinal).FirstOrDefault(x => x.Count() > 1);
        if (duplicate != null) yield return new("error", "SOURCE_XML_INVALID", $"Duplicate Siemens ID '{duplicate.Key}'.");
    }

    private static string ProtectedCanonical(XDocument document)
    {
        var clone = new XDocument(document);
        var block = FindBlock(clone);
        var owners = CompileUnits(block).Append(block).ToArray();
        foreach (var owner in owners)
        {
            var objectList = owner.Elements().FirstOrDefault(x => x.Name.LocalName == "ObjectList");
            objectList?.Elements().Where(x => x.Name.LocalName == "MultilingualText" &&
                (Attr(x, "CompositionName") == "Title" || Attr(x, "CompositionName") == "Comment")).Remove();
        }
        var attributes = Child(block, "AttributeList");
        foreach (var name in new[] { "HeaderAuthor", "HeaderFamily", "HeaderName" })
            foreach (var element in attributes.Elements().Where(x => x.Name.LocalName == name))
                element.Value = "__EDITABLE_TEXT__";
        foreach (var element in clone.Root!.DescendantsAndSelf())
            element.Attributes().OrderBy(x => x.Name.ToString(), StringComparer.Ordinal).ToList().ForEach(x => { x.Remove(); element.Add(x); });
        return clone.ToString(SaveOptions.DisableFormatting).Replace("\r\n", "\n");
    }

    private static Dictionary<string, string> EditableFields(XDocument document)
    {
        var block = FindBlock(document);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var owners = CompileUnits(block).Select((x, i) => (Element: x, Kind: "network", Number: (int?)i + 1))
            .Append((block, "block", null));
        foreach (var owner in owners)
            foreach (var field in new[] { "Title", "Comment" })
                foreach (var value in TextValues(owner.Element, field))
                    result[$"{owner.Kind}|{Attr(owner.Element, "ID")}|{owner.Number}|{field}|{value.Culture}"] = value.Value;
        var blockAttributes = Child(block, "AttributeList");
        foreach (var pair in new[]
        {
            ("blockHeaderAuthor", "HeaderAuthor"),
            ("blockHeaderFamily", "HeaderFamily"),
            ("blockHeaderName", "HeaderName"),
        })
            if (blockAttributes.Elements().FirstOrDefault(x => x.Name.LocalName == pair.Item2) is { } element)
                result[$"block|{Attr(block, "ID")}||{pair.Item1}|"] = element.Value;
        return result;
    }

    private static bool ExistingEditableStructureMatches(XDocument baseline, XDocument candidate)
    {
        static Dictionary<string, string> Identities(XDocument document)
        {
            var block = FindBlock(document);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var owner in CompileUnits(block).Append(block))
            {
                var objectList = owner.Elements().FirstOrDefault(x => x.Name.LocalName == "ObjectList");
                foreach (var composition in objectList?.Elements().Where(x => x.Name.LocalName == "MultilingualText"
                    && Attr(x, "CompositionName") is "Title" or "Comment") ?? Enumerable.Empty<XElement>())
                {
                    var field = Attr(composition, "CompositionName");
                    var compositionIndex = objectList!.Elements().TakeWhile(x => !ReferenceEquals(x, composition)).Count();
                    foreach (var item in composition.Descendants().Where(x => x.Name.LocalName == "MultilingualTextItem"))
                    {
                        var attributes = Child(item, "AttributeList");
                        var culture = Value(attributes, "Culture") ?? "";
                        var clone = new XElement(composition);
                        var clonedList = clone.Descendants().First(x => x.Name.LocalName == "ObjectList");
                        clonedList.Elements().Where(x => x.Name.LocalName == "MultilingualTextItem"
                            && Value(Child(x, "AttributeList"), "Culture") != culture).Remove();
                        foreach (var text in clone.Descendants().Where(x => x.Name.LocalName == "Text"))
                            text.Value = "__EDITABLE_TEXT__";
                        result[$"{Attr(owner, "ID")}|{field}|{culture}"] =
                            $"{compositionIndex}|{clone.ToString(SaveOptions.DisableFormatting)}";
                    }
                }
            }
            return result;
        }

        var before = Identities(baseline);
        var after = Identities(candidate);
        if (!before.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value))
            return false;

        static string ItemShape(XElement item)
        {
            var clone = new XElement(item);
            clone.SetAttributeValue("ID", "__NEW_ID__");
            var attributes = clone.Elements().FirstOrDefault(x => x.Name.LocalName == "AttributeList");
            var culture = attributes?.Elements().FirstOrDefault(x => x.Name.LocalName == "Culture");
            var text = attributes?.Elements().FirstOrDefault(x => x.Name.LocalName == "Text");
            if (attributes == null || culture == null || text == null)
                return "__INVALID_ITEM__|" + clone.ToString(SaveOptions.DisableFormatting);
            culture.Value = "__CULTURE__";
            text.Value = "__EDITABLE_TEXT__";
            return clone.ToString(SaveOptions.DisableFormatting);
        }

        var baselineShapes = FindBlock(baseline).Descendants()
            .Where(x => x.Name.LocalName == "MultilingualTextItem").Select(ItemShape)
            .ToHashSet(StringComparer.Ordinal);
        var baselineKeys = before.Keys.ToHashSet(StringComparer.Ordinal);
        var candidateBlock = FindBlock(candidate);
        foreach (var owner in CompileUnits(candidateBlock).Append(candidateBlock))
        {
            var objectList = owner.Elements().FirstOrDefault(x => x.Name.LocalName == "ObjectList");
            foreach (var composition in objectList?.Elements().Where(x => x.Name.LocalName == "MultilingualText"
                && Attr(x, "CompositionName") is "Title" or "Comment") ?? Enumerable.Empty<XElement>())
            {
                var field = Attr(composition, "CompositionName");
                foreach (var item in composition.Descendants().Where(x => x.Name.LocalName == "MultilingualTextItem"))
                {
                    var culture = Value(Child(item, "AttributeList"), "Culture") ?? "";
                    var key = $"{Attr(owner, "ID")}|{field}|{culture}";
                    if (!baselineKeys.Contains(key) && !baselineShapes.Contains(ItemShape(item)))
                        return false;
                }
            }
        }
        static Dictionary<string, List<XElement>> Compositions(XDocument document)
        {
            var block = FindBlock(document);
            var result = new Dictionary<string, List<XElement>>(StringComparer.Ordinal);
            foreach (var owner in CompileUnits(block).Append(block))
            {
                var list = owner.Elements().FirstOrDefault(x => x.Name.LocalName == "ObjectList");
                foreach (var composition in list?.Elements().Where(x => x.Name.LocalName == "MultilingualText"
                    && Attr(x, "CompositionName") is "Title" or "Comment") ?? Enumerable.Empty<XElement>())
                {
                    var key = $"{Attr(owner, "ID")}|{Attr(composition, "CompositionName")}";
                    if (!result.TryGetValue(key, out var values)) result[key] = values = new();
                    values.Add(composition);
                }
            }
            return result;
        }
        static string CompositionShape(XElement composition)
        {
            var clone = new XElement(composition);
            clone.SetAttributeValue("ID", "__NEW_ID__");
            clone.Descendants().First(x => x.Name.LocalName == "ObjectList").Elements()
                .Where(x => x.Name.LocalName == "MultilingualTextItem").Remove();
            return clone.ToString(SaveOptions.DisableFormatting);
        }

        var baselineCompositions = Compositions(baseline);
        var candidateCompositions = Compositions(candidate);
        var approvedShapes = baselineCompositions.Values.SelectMany(x => x)
            .GroupBy(x => Attr(x, "CompositionName"), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(CompositionShape).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        if (baselineCompositions.Any(pair =>
            !candidateCompositions.TryGetValue(pair.Key, out var values) || values.Count != pair.Value.Count))
            return false;
        foreach (var pair in candidateCompositions)
        {
            var baselineCount = baselineCompositions.GetValueOrDefault(pair.Key)?.Count ?? 0;
            if (baselineCount > 0 && pair.Value.Count != baselineCount) return false;
            if (baselineCount == 0)
            {
                if (pair.Value.Count != 1) return false;
                var composition = pair.Value[0];
                var field = Attr(composition, "CompositionName");
                if (!composition.Descendants().Any(x => x.Name.LocalName == "MultilingualTextItem")) return false;
                if (!approvedShapes.TryGetValue(field, out var shapes) || !shapes.Contains(CompositionShape(composition)))
                    return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<MultilingualValue> TextValues(XElement owner, string field) =>
        owner.Elements().FirstOrDefault(x => x.Name.LocalName == "ObjectList")?.Elements()
            .Where(x => x.Name.LocalName == "MultilingualText" && Attr(x, "CompositionName") == field)
            .SelectMany(x => x.Descendants().Where(y => y.Name.LocalName == "MultilingualTextItem"))
            .Select(x =>
            {
                var a = Child(x, "AttributeList");
                return new MultilingualValue(Value(a, "Culture") ?? "", Value(a, "Text") ?? "");
            }).ToArray() ?? Array.Empty<MultilingualValue>();

    private LoadedXml Load(string path, string parameter)
    {
        var canonical = jail.Validate(path, parameter);
        if (!canonical.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw new SourceEditorException("SOURCE_XML_INVALID", $"{parameter} must be an XML file.");
        if (!File.Exists(canonical))
            throw new SourceEditorException("SOURCE_FILE_NOT_FOUND", $"XML file does not exist: {canonical}");
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(canonical, settings);
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            return new(canonical, document, DetectEncoding(canonical, document));
        }
        catch (SourceEditorException) { throw; }
        catch (Exception ex)
        {
            throw new SourceEditorException("SOURCE_XML_INVALID", $"Cannot parse XML: {ex.Message}", inner: ex);
        }
    }

    private static XElement FindBlock(XDocument document) =>
        document.Descendants().FirstOrDefault(x => x.Name.LocalName is "SW.Blocks.OB" or "SW.Blocks.FB" or "SW.Blocks.FC" or "SW.Blocks.DB")
        ?? throw new SourceEditorException("SOURCE_BLOCK_UNSUPPORTED", "No supported TIA block element was found.");

    private static List<XElement> CompileUnits(XElement block) =>
        block.Descendants().Where(x => x.Name.LocalName == "SW.Blocks.CompileUnit").ToList();
    private static XElement Child(XElement owner, string name) =>
        owner.Elements().First(x => x.Name.LocalName == name);
    private static string? Value(XElement owner, string name) =>
        owner.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value;
    private static void SetValue(XElement owner, string name, string value)
    {
        var element = owner.Elements().FirstOrDefault(x => x.Name.LocalName == name)
            ?? new XElement(owner.GetDefaultNamespace() + name);
        if (element.Parent == null) owner.Add(element);
        element.Value = value;
    }
    private static string Attr(XElement owner, string name) => (string?)owner.Attribute(name) ?? "";
    private static string? NormalizeCulture(string? culture, int index)
    {
        if (culture == null) return null;
        try { return CultureInfo.GetCultureInfo(culture).Name; }
        catch (CultureNotFoundException) { throw Error("SOURCE_CULTURE_INVALID", $"Culture '{culture}' is invalid.", index); }
    }
    private static string NextId(XDocument document)
    {
        var max = document.Descendants().Attributes("ID")
            .Select(x => long.TryParse(x.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .DefaultIfEmpty(-1).Max();
        return (max + 1).ToString("X", CultureInfo.InvariantCulture);
    }
    private static string DefaultOutput(string path, string suffix) =>
        Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + suffix + Path.GetExtension(path));
    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static string WriteTemporary(XDocument document, string output, Encoding encoding)
    {
        var temp = Path.Combine(Path.GetDirectoryName(output)!,
            $".{Path.GetFileNameWithoutExtension(output)}.{Guid.NewGuid():N}.xml");
        try
        {
            using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough);
            using (var writer = XmlWriter.Create(stream,
                new XmlWriterSettings { Encoding = encoding, Indent = false, OmitXmlDeclaration = false, CloseOutput = false }))
                document.Save(writer);
            stream.Flush(flushToDisk: true);
            return temp;
        }
        catch (Exception ex)
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw new SourceEditorException("SOURCE_WRITE_FAILED", $"Could not write XML: {ex.Message}", inner: ex);
        }
    }
    private static Encoding DetectEncoding(string path, XDocument document)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(true);
        if (!string.IsNullOrWhiteSpace(document.Declaration?.Encoding))
        {
            try { return Encoding.GetEncoding(document.Declaration.Encoding); }
            catch (ArgumentException) { }
        }
        return new UTF8Encoding(false);
    }
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static SourceEditorException Error(string code, string message, int index) => new(code, message, batchIndex: index);
    private sealed record LoadedXml(string Path, XDocument Document, Encoding Encoding);
}
