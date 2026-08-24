namespace PlcXml.Model;

public sealed class PlcDocument
{
    internal PlcDocument(byte[] originalBytes, string? sourceName, IReadOnlyList<PlcObject> objects,
        IReadOnlyList<PlcRawValue> rawValues, string encodingName, bool hasBom, bool usesCrLf)
    {
        OriginalBytes = originalBytes; SourceName = sourceName; Objects = objects; RawValues = rawValues;
        EncodingName = encodingName; HasBom = hasBom; UsesCrLf = usesCrLf;
    }
    internal byte[] OriginalBytes { get; }
    public string? SourceName { get; }
    public IReadOnlyList<PlcObject> Objects { get; }
    public IReadOnlyList<PlcRawValue> RawValues { get; }
    public string EncodingName { get; }
    public bool HasBom { get; }
    public bool UsesCrLf { get; }
    public byte[] SerializeOriginal() => (byte[])OriginalBytes.Clone();
}
