using System.Xml.Linq;

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
    }
    private readonly byte[] OriginalBytes;
    private readonly XDocument _originalTree;
    public string? SourceName { get; }
    public IReadOnlyList<PlcObject> Objects { get; }
    public IReadOnlyList<PlcRawValue> RawValues { get; }
    public IReadOnlyList<PlcNode> Children { get; }
    public string EncodingName { get; }
    public bool HasBom { get; }
    public bool UsesCrLf { get; }
    public byte[] SerializeOriginal() => (byte[])OriginalBytes.Clone();
}
