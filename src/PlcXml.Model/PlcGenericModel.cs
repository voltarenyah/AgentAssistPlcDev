using System.Xml.Linq;

namespace PlcXml.Model;

public sealed class PlcLocation
{
    internal PlcLocation(string path, string? id) { Path = path; Id = id; }
    public string Path { get; }
    public string? Id { get; }
    public override string ToString() => Path;
}

public abstract class PlcNode
{
    protected PlcNode(string name) { Name = name; }
    public string Name { get; }
}

public sealed class PlcObject : PlcNode
{
    internal PlcObject(string elementName, string? id, PlcLocation location,
        IReadOnlyList<PlcAttribute> attributes, IReadOnlyList<PlcObject> compositions,
        IReadOnlyList<PlcRawValue> rawValues, IReadOnlyDictionary<string, string> xmlAttributes,
        IReadOnlyList<PlcNode> children) : base(elementName)
    {
        ElementName = elementName; Id = id; Location = location; Attributes = attributes;
        Compositions = compositions; RawValues = rawValues; XmlAttributes = xmlAttributes; Children = children;
    }
    public string ElementName { get; }
    public string? Id { get; }
    public PlcLocation Location { get; }
    public IReadOnlyList<PlcAttribute> Attributes { get; }
    public IReadOnlyList<PlcObject> Compositions { get; }
    public IReadOnlyList<PlcObject> Objects => Compositions;
    public IReadOnlyList<PlcRawValue> RawValues { get; }
    public IReadOnlyDictionary<string, string> XmlAttributes { get; }
    public IReadOnlyList<PlcNode> Children { get; }
}

public sealed class PlcAttribute : PlcNode
{
    internal PlcAttribute(string name, string value, XElement? rawElement = null) : base(name)
    { Value = value; RawElement = rawElement is null ? null : new XElement(rawElement); }
    public string Value { get; }
    public XElement? RawValue => RawElement is null ? null : new XElement(RawElement);
    internal XElement? RawElement { get; }
}

public sealed class PlcRawValue : PlcNode
{
    internal PlcRawValue(XElement element) : base(element.Name.LocalName) { _element = new XElement(element); }
    private readonly XElement _element;
    public XElement Element => new XElement(_element);
}
