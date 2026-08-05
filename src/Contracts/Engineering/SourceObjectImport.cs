namespace Contracts.Engineering;

public enum SourceObjectKind
{
    Block,
    TagTable,
    Udt,
}

public sealed class SourceObjectImportResult
{
    public string RelativePath { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public string ObjectKind { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string[] Warnings { get; set; } = Array.Empty<string>();
    public string? Error { get; set; }
}

public static class SourceObjectImport
{
    public static SourceObjectKind Classify(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("PLC source path is required.", nameof(relativePath));

        var normalized = relativePath.Replace("\\", "/");
        if (normalized.StartsWith("/") || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("PLC source path must be relative and non-traversing.", nameof(relativePath));

        return normalized.Split('/')[0] switch
        {
            "Blocks" or "DB" => SourceObjectKind.Block,
            "Tags" => SourceObjectKind.TagTable,
            "UDT" => SourceObjectKind.Udt,
            _ => throw new ArgumentException("Unsupported PLC source category.", nameof(relativePath)),
        };
    }
}
