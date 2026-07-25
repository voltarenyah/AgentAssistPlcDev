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
            HashFile(source.Path));
    }

    public EditBatchResult Preview(string xmlFilePath, IReadOnlyList<SourceEdit> edits,
        string? outputFilePath, bool overwriteOutput) =>
        WriteEdited(xmlFilePath, edits, outputFilePath ?? DefaultOutput(xmlFilePath, ".preview"), overwriteOutput, false);

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
                && ExistingEditableIdsMatch(baseline.Document, candidate.Document);
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
        AtomicWrite(candidate, output, source.Encoding, inPlace || overwrite);
        var reopened = Load(output, nameof(outputFilePath));
        var reopenedValidation = ValidateDocuments(source.Document, reopened.Document);
        if (!reopenedValidation.IsValid)
            throw new SourceEditorException("SOURCE_INTEGRITY_CHANGED", "Serialized output changed protected PLC content.");
        return new(source.Path, output, normalized, reopenedValidation, true, HashFile(source.Path), HashFile(output));
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
            throw Error("SOURCE_TEMPLATE_MISSING", $"{field} structure is missing for target {Attr(owner, "ID")}.", index);
        var objectList = composition.Elements().First(x => x.Name.LocalName == "ObjectList");
        var items = objectList.Elements().Where(x => x.Name.LocalName == "MultilingualTextItem").ToList();
        XElement? item = requestedCulture == null ? items.FirstOrDefault() : items.FirstOrDefault(x => Value(Child(x, "AttributeList"), "Culture") == requestedCulture);
        actualCulture = requestedCulture ?? (item == null ? "en-US" : Value(Child(item, "AttributeList"), "Culture") ?? "en-US");
        if (item == null)
        {
            var template = items.FirstOrDefault() ?? document.Descendants().FirstOrDefault(x => x.Name.LocalName == "MultilingualTextItem");
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
        oldValue = Value(attributes, xmlName) ?? "";
        SetValue(attributes, xmlName, value);
        return propertyName;
    }

    private static SourceValidationResult ValidateDocuments(XDocument baseline, XDocument candidate)
    {
        var findings = StandaloneFindings(candidate).ToList();
        var matches = ProtectedCanonical(baseline) == ProtectedCanonical(candidate)
            && ExistingEditableIdsMatch(baseline, candidate);
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
            attributes.Elements().Where(x => x.Name.LocalName == name).Remove();
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
        return result;
    }

    private static bool ExistingEditableIdsMatch(XDocument baseline, XDocument candidate)
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
                    foreach (var item in composition.Descendants().Where(x => x.Name.LocalName == "MultilingualTextItem"))
                    {
                        var attributes = Child(item, "AttributeList");
                        var culture = Value(attributes, "Culture") ?? "";
                        result[$"{Attr(owner, "ID")}|{field}|{culture}"] = $"{Attr(composition, "ID")}:{Attr(item, "ID")}";
                    }
                }
            }
            return result;
        }

        var before = Identities(baseline);
        var after = Identities(candidate);
        return before.All(pair => after.TryGetValue(pair.Key, out var value) && value == pair.Value);
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
            return new(canonical, document, new UTF8Encoding(false));
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
    private static void AtomicWrite(XDocument document, string output, Encoding encoding, bool overwrite)
    {
        var temp = Path.Combine(Path.GetDirectoryName(output)!, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var writer = XmlWriter.Create(temp, new XmlWriterSettings { Encoding = encoding, Indent = false, OmitXmlDeclaration = false }))
                document.Save(writer);
            File.Move(temp, output, overwrite);
        }
        catch (Exception ex)
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw new SourceEditorException("SOURCE_WRITE_FAILED", $"Could not write XML: {ex.Message}", inner: ex);
        }
    }
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static SourceEditorException Error(string code, string message, int index) => new(code, message, batchIndex: index);
    private sealed record LoadedXml(string Path, XDocument Document, Encoding Encoding);
}
