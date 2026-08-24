using System.Collections.ObjectModel;
using System.Xml.Linq;

namespace PlcXml.Model;

public abstract class PlcTypedPayload
{
    protected PlcTypedPayload(string name) => Name = name;
    public string Name { get; }
}

public sealed class PlcInterface : PlcTypedPayload
{
    internal PlcInterface(IEnumerable<PlcInterfaceSection> sections, IEnumerable<PlcRawValue> rawValues)
        : base("Interface")
    {
        Sections = new ReadOnlyCollection<PlcInterfaceSection>(sections.ToList());
        RawValues = new ReadOnlyCollection<PlcRawValue>(rawValues.ToList());
    }
    public IReadOnlyList<PlcInterfaceSection> Sections { get; }
    public IReadOnlyList<PlcRawValue> RawValues { get; }
    public IReadOnlyList<PlcInterfaceSection> Items => Sections;
}

public sealed class PlcInterfaceSection
{
    internal PlcInterfaceSection(string name, IReadOnlyDictionary<string, string> attributes,
        IEnumerable<PlcInterfaceMember> members, IEnumerable<PlcRawValue> rawValues)
    {
        Name = name; Attributes = attributes;
        Members = new ReadOnlyCollection<PlcInterfaceMember>(members.ToList());
        RawValues = new ReadOnlyCollection<PlcRawValue>(rawValues.ToList());
    }
    public string Name { get; }
    public IReadOnlyDictionary<string, string> Attributes { get; }
    public IReadOnlyList<PlcInterfaceMember> Members { get; }
    public IReadOnlyList<PlcRawValue> RawValues { get; }
}

public sealed class PlcInterfaceMember
{
    internal PlcInterfaceMember(IReadOnlyDictionary<string, string> attributes, IEnumerable<PlcRawValue> rawValues, string? comment)
    {
        Attributes = attributes; Name = attributes.TryGetValue("Name", out var name) ? name : null;
        RawValues = new ReadOnlyCollection<PlcRawValue>(rawValues.ToList()); Comment = comment;
    }
    public string? Name { get; }
    public IReadOnlyDictionary<string, string> Attributes { get; }
    public IReadOnlyList<PlcRawValue> RawValues { get; }
    public string? Comment { get; }
}

public sealed class LadderNetwork : PlcTypedPayload
{
    internal LadderNetwork(IEnumerable<LadderAccess> accesses, IEnumerable<LadderPart> parts,
        IEnumerable<LadderCall> calls, IEnumerable<LadderWire> wires, IEnumerable<RawFlgNode> rawNodes)
        : base("FlgNet")
    {
        Accesses = new ReadOnlyCollection<LadderAccess>(accesses.ToList()); Parts = new ReadOnlyCollection<LadderPart>(parts.ToList());
        Calls = new ReadOnlyCollection<LadderCall>(calls.ToList()); Wires = new ReadOnlyCollection<LadderWire>(wires.ToList());
        RawNodes = new ReadOnlyCollection<RawFlgNode>(rawNodes.ToList());
    }
    public IReadOnlyList<LadderAccess> Accesses { get; }
    public IReadOnlyList<LadderPart> Parts { get; }
    public IReadOnlyList<LadderCall> Calls { get; }
    public IReadOnlyList<LadderWire> Wires { get; }
    public IReadOnlyList<RawFlgNode> RawNodes { get; }
    public IReadOnlyList<RawFlgNode> RawValues => RawNodes;
}

public abstract class LadderNode
{
    private readonly XElement _source;
    protected LadderNode(string name, XElement source) { Name = name; _source = new XElement(source); }
    public string Name { get; }
    public XElement Source => new XElement(_source);
}
public sealed class LadderAccess : LadderNode
{
    internal LadderAccess(XElement source) : base("Access", source) { Attributes = AttributesOf(source); }
    public IReadOnlyDictionary<string, string> Attributes { get; }
    private static IReadOnlyDictionary<string, string> AttributesOf(XElement e) =>
        new ReadOnlyDictionary<string, string>(e.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value));
}
public sealed class LadderPart : LadderNode
{
    internal LadderPart(XElement source) : base("Part", source) { Attributes = source.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value); }
    public IReadOnlyDictionary<string, string> Attributes { get; }
}
public sealed class LadderCall : LadderNode
{
    internal LadderCall(XElement source) : base("Call", source) { Attributes = source.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value); }
    public IReadOnlyDictionary<string, string> Attributes { get; }
}
public sealed class LadderWire : LadderNode
{
    internal LadderWire(XElement source) : base(source.Name.LocalName, source) { Attributes = source.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value); }
    public IReadOnlyDictionary<string, string> Attributes { get; }
}
public sealed class RawFlgNode : LadderNode
{
    internal RawFlgNode(XElement source) : base(source.Name.LocalName, source) { }
}

public sealed class StructuredTextNetwork : PlcTypedPayload
{
    internal StructuredTextNetwork(IEnumerable<StEntry> entries) : base("StructuredText")
        => Entries = new ReadOnlyCollection<StEntry>(entries.ToList());
    public IReadOnlyList<StEntry> Entries { get; }
    public IReadOnlyList<StEntry> Nodes => Entries;
    public IReadOnlyList<StRaw> RawValues => Entries.OfType<StRaw>().ToList();
}
public abstract class StEntry
{
    private readonly XElement _source;
    protected StEntry(string name, XElement source) { Name = name; _source = new XElement(source); }
    public string Name { get; }
    public XElement Source => new XElement(_source);
}
public sealed class StToken : StEntry { internal StToken(XElement e) : base("Token", e) { Text = (string?)e.Attribute("Text"); } public string? Text { get; } }
public sealed class StBlank : StEntry { internal StBlank(XElement e) : base("Blank", e) { Number = (string?)e.Attribute("Num"); } public string? Number { get; } }
public sealed class StNewLine : StEntry { internal StNewLine(XElement e) : base("NewLine", e) { Number = (string?)e.Attribute("Num"); } public string? Number { get; } }
public sealed class StAccess : StEntry { internal StAccess(XElement e) : base("Access", e) { Attributes = e.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value); } public IReadOnlyDictionary<string, string> Attributes { get; } }
public sealed class StRaw : StEntry { internal StRaw(XElement e) : base(e.Name.LocalName, e) { } }
