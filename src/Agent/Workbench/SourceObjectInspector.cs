using System.Xml;
using System.Xml.Linq;

namespace Agent.Workbench;

public sealed record SourceInspection(
    string Category,
    string Name,
    string RelativePath,
    IReadOnlyList<SourceInspectionSection> Interfaces,
    IReadOnlyList<SourceInspectionTable> Tables,
    IReadOnlyList<SourceInspectionNetwork> Networks);

public sealed record SourceInspectionSection(string Name, IReadOnlyList<SourceInspectionMember> Members);
public sealed record SourceInspectionMember(string Name, string? DataType, string? DefaultValue, string? Accessibility, string? Comment);
public sealed record SourceInspectionTable(string Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows);
public sealed record SourceInspectionNetwork(int Index, string CompileUnitId, string? Title, string? Comment, string? Language, string? StructuredText, LadderNetworkInspection? Ladder);
public sealed record LadderNetworkInspection(IReadOnlyList<LadderElementInspection> Elements, IReadOnlyList<LadderWireInspection> Wires);
public sealed record LadderElementInspection(string Id, string Kind, string? Label, IReadOnlyList<string> NegatedPins, string? ReferencedObject, IReadOnlyList<LadderPinInspection> Pins);
public sealed record LadderPinInspection(string Name, string? Label, string? ReferencedObject, bool Negated);
public sealed record LadderWireInspection(IReadOnlyList<LadderEndpointInspection> Endpoints);
public sealed record LadderEndpointInspection(string Kind, string? ElementId, string? Pin);

/// <summary>Projects one trusted device XML source file into a read-only inspector shape.
/// The projection is intentionally source-first: knowledge data may discover a network, but never
/// supplies the visual XML topology shown to the user.</summary>
public sealed class SourceObjectInspectorReader
{
    public SourceInspection Read(DeviceContext context, string relativePath, DeviceSourceResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolver);
        var path = resolver.ResolveEffective(context, relativePath);
        var document = Load(path);
        var root = document.Root?.Name.LocalName == "Document"
            ? document.Root.Elements().FirstOrDefault(element => element.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal))
            : document.Root;
        if (root is null)
            throw new SourceInspectionException("SOURCE_INSPECTION_UNSUPPORTED", "The XML file has no supported Siemens source object.");

        var category = Category(root.Name.LocalName);
        if (category is null)
            throw new SourceInspectionException("SOURCE_INSPECTION_UNSUPPORTED", $"The XML root '{root.Name.LocalName}' is not inspectable.");
        var name = Value(Child(root, "AttributeList"), "Name") ?? Path.GetFileNameWithoutExtension(relativePath);
        return new SourceInspection(category, name, relativePath.Replace('\\', '/'),
            category == "Blocks" ? Interfaces(root) : [], Tables(root, category), category == "Blocks" ? Networks(root) : []);
    }

    private static XDocument Load(string path)
    {
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex) { throw new SourceInspectionException("SOURCE_INSPECTION_XML_INVALID", "The selected source XML is malformed.", ex); }
    }

    private static IReadOnlyList<SourceInspectionSection> Interfaces(XElement root)
    {
        var sections = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "Interface")?
            .Descendants().FirstOrDefault(element => element.Name.LocalName == "Sections");
        return sections?.Elements().Where(element => element.Name.LocalName == "Section").Select(section =>
            new SourceInspectionSection((string?)section.Attribute("Name") ?? "Interface", section.Elements()
                .Where(member => member.Name.LocalName == "Member").Select(member => new SourceInspectionMember(
                    (string?)member.Attribute("Name") ?? "", (string?)member.Attribute("Datatype"),
                    (string?)member.Attribute("DefaultValue"), (string?)member.Attribute("Accessibility"),
                    member.Elements().FirstOrDefault(item => item.Name.LocalName == "Comment")?.Value)).ToArray())).ToArray() ?? [];
    }

    private static IReadOnlyList<SourceInspectionTable> Tables(XElement root, string category)
    {
        if (category == "Tags")
        {
            var rows = root.Descendants().Where(element => element.Name.LocalName is "PlcTag" or "Tag" || element.Name.LocalName.EndsWith(".PlcTag", StringComparison.Ordinal))
                .Select(element => Row(("Name", AttrOrValue(element, "Name")), ("Data type", AttrOrValue(element, "Datatype") ?? AttrOrValue(element, "DataTypeName")), ("Address", AttrOrValue(element, "LogicalAddress")), ("Comment", Text(element, "Comment")))).ToArray();
            return [new SourceInspectionTable("Tags", ["Name", "Data type", "Address", "Comment"], rows)];
        }
        var members = root.Descendants().Where(element => element.Name.LocalName == "Member")
            .Select(element => Row(("Path", (string?)element.Attribute("Name") ?? ""), ("Data type", (string?)element.Attribute("Datatype")), ("Comment", element.Elements().FirstOrDefault(item => item.Name.LocalName == "Comment")?.Value))).ToArray();
        return [new SourceInspectionTable(category == "UDT" ? "Members" : "Data", ["Path", "Data type", "Comment"], members)];
    }

    private static IReadOnlyList<SourceInspectionNetwork> Networks(XElement root) => root.Descendants()
        .Where(element => element.Name.LocalName == "SW.Blocks.CompileUnit").Select((unit, offset) =>
        {
            var attributes = Child(unit, "AttributeList");
            var language = Value(attributes, "ProgrammingLanguage");
            var flgNet = unit.Descendants().FirstOrDefault(element => element.Name.LocalName == "FlgNet");
            var structured = unit.Descendants().FirstOrDefault(element => element.Name.LocalName == "StructuredText");
            return new SourceInspectionNetwork(offset + 1, (string?)unit.Attribute("ID") ?? offset.ToString(), Text(unit, "Title"), Text(unit, "Comment"), language,
                structured is null ? null : string.Concat(structured.Descendants().Where(node => node.Name.LocalName == "Token").Select(node => (string?)node.Attribute("Text") ?? node.Value)),
                flgNet is null ? null : Ladder(flgNet));
        }).ToArray();

    private static LadderNetworkInspection Ladder(XElement flgNet)
    {
        var parts = flgNet.Descendants().FirstOrDefault(element => element.Name.LocalName == "Parts");
        var wires = flgNet.Descendants().Where(element => element.Name.LocalName == "Wire").Select(wire => new LadderWireInspection(wire.Elements().Select(endpoint =>
            new LadderEndpointInspection(endpoint.Name.LocalName, (string?)endpoint.Attribute("UId"), (string?)endpoint.Attribute("Name"))).ToArray())).ToArray();
        Dictionary<string, (string? Label, string? ReferencedObject)> accesses = parts?.Elements().Where(element => element.Name.LocalName == "Access")
            .ToDictionary(element => (string?)element.Attribute("UId") ?? "", element =>
            {
                var reference = element.Descendants().FirstOrDefault(node => node.Name.LocalName == "Component")?.Attribute("Name")?.Value;
                return (reference ?? element.Descendants().FirstOrDefault(node => node.Name.LocalName == "ConstantValue")?.Value, reference);
            }) ?? [];
        var elements = parts?.Elements().Where(element => element.Name.LocalName is "Part" or "Call").Select(element =>
        {
            var id = (string?)element.Attribute("UId") ?? Guid.NewGuid().ToString("N");
            var kind = (string?)element.Attribute("Name") ?? element.Name.LocalName;
            var negated = element.Elements().Where(node => node.Name.LocalName == "Negated").Select(node => (string?)node.Attribute("Name") ?? "operand").ToArray();
            var pins = wires.SelectMany(wire => wire.Endpoints
                .Where(endpoint => endpoint.Kind == "NameCon" && endpoint.ElementId == id && endpoint.Pin is not null)
                .Select(endpoint =>
                {
                    var accessId = wire.Endpoints.FirstOrDefault(candidate => candidate.Kind == "IdentCon")?.ElementId;
                    var access = accessId is not null && accesses.TryGetValue(accessId, out var value) ? value : (null, null);
                    return new LadderPinInspection(endpoint.Pin!, access.Item1, access.Item2,
                        negated.Contains(endpoint.Pin!, StringComparer.OrdinalIgnoreCase));
                }))
                .GroupBy(pin => pin.Name, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
            var operand = pins.FirstOrDefault(pin => string.Equals(pin.Name, "operand", StringComparison.OrdinalIgnoreCase));
            var label = operand?.Label ??
                element.Descendants().FirstOrDefault(node => node.Name.LocalName == "Component")?.Attribute("Name")?.Value ?? kind;
            var reference = operand?.ReferencedObject ??
                element.Descendants().FirstOrDefault(node => node.Name.LocalName == "Component")?.Attribute("Name")?.Value;
            return new LadderElementInspection(id, kind, label, negated, reference, pins);
        }).ToArray() ?? [];
        return new LadderNetworkInspection(elements, wires);
    }

    private static string? Category(string root) => root switch
    {
        "SW.Blocks.OB" or "SW.Blocks.FB" or "SW.Blocks.FC" => "Blocks",
        "SW.Blocks.DB" or "SW.Blocks.GlobalDB" or "SW.Blocks.InstanceDB" or "SW.Blocks.ArrayDB" => "DB",
        "SW.Tags.PlcTagTable" => "Tags",
        "SW.Types.PlcStruct" or "SW.Types.PlcEnum" or "SW.Types.PlcArray" or "SW.Types.PlcType" => "UDT",
        _ => null,
    };
    private static XElement? Child(XElement owner, string name) => owner.Elements().FirstOrDefault(element => element.Name.LocalName == name);
    private static string? Value(XElement? owner, string name) => owner?.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value;
    private static string? AttrOrValue(XElement owner, string name) => (string?)owner.Attribute(name) ?? Value(Child(owner, "AttributeList"), name);
    private static string? Text(XElement owner, string composition) => owner.Descendants().FirstOrDefault(element => element.Name.LocalName == "MultilingualText" && (string?)element.Attribute("CompositionName") == composition)?
        .Descendants().FirstOrDefault(element => element.Name.LocalName == "Text")?.Value;
    private static IReadOnlyDictionary<string, string?> Row(params (string Key, string? Value)[] values) => values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal);
}

public sealed class SourceInspectionException : Exception
{
    public SourceInspectionException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
    public string Code { get; }
}
