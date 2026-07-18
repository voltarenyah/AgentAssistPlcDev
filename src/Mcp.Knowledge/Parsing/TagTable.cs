// Ported from PlcSourceExporter (src/PlcSourceExporter.Core/TagTable.cs) — adapted for mcp-knowledge; keep changes minimal to ease future re-syncs.
// Only the reader side ports: TagTableRow + TagTableBuilder.ParseRows. The export side
// (TagTableDocument, ExportedTagTableFile, Write, TagTableJsonSerializer) is mcp-engineering's job.
using System.Xml;
using System.Xml.Linq;

namespace Mcp.Knowledge.Parsing;

public sealed class TagTableRow
{
    public TagTableRow(
        string id,
        string tagTable,
        string tagTableSourcePath,
        string name,
        string dataType,
        string rawDataType,
        string logicalAddress,
        bool? externalAccessible,
        bool? externalVisible,
        bool? externalWritable,
        string comment,
        string sourceFile)
    {
        Id = id;
        TagTable = tagTable;
        TagTableSourcePath = tagTableSourcePath;
        Name = name;
        DataType = dataType;
        RawDataType = rawDataType;
        LogicalAddress = logicalAddress;
        ExternalAccessible = externalAccessible;
        ExternalVisible = externalVisible;
        ExternalWritable = externalWritable;
        Comment = comment;
        SourceFile = sourceFile;
    }

    public string Id { get; }

    public string TagTable { get; }

    public string TagTableSourcePath { get; }

    public string Name { get; }

    public string DataType { get; }

    public string RawDataType { get; }

    public string LogicalAddress { get; }

    public bool? ExternalAccessible { get; }

    public bool? ExternalVisible { get; }

    public bool? ExternalWritable { get; }

    public string Comment { get; }

    public string SourceFile { get; }
}

public static class TagTableBuilder
{
    public static IReadOnlyList<TagTableRow> ParseRows(string xml, string sourceFile, string tagTableSourcePath)
    {
        if (xml == null)
        {
            throw new ArgumentNullException(nameof(xml));
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return Array.Empty<TagTableRow>();
        }

        var tagTable = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "SW.Tags.PlcTagTable");
        if (tagTable == null)
        {
            return Array.Empty<TagTableRow>();
        }

        var rawTagTableName = GetDirectAttributeValue(tagTable, "Name");
        var tagTableName = string.IsNullOrWhiteSpace(rawTagTableName) ? null : rawTagTableName;
        if (tagTableName == null)
        {
            tagTableName = Path.GetFileNameWithoutExtension(sourceFile) ?? string.Empty;
        }

        var rows = new List<TagTableRow>();
        foreach (var tag in tagTable.Descendants().Where(element => element.Name.LocalName == "SW.Tags.PlcTag"))
        {
            var rawTagName = GetDirectAttributeValue(tag, "Name");
            if (string.IsNullOrWhiteSpace(rawTagName))
            {
                continue;
            }

            var tagName = rawTagName!;
            var rawDataType = GetDirectAttributeValue(tag, "DataTypeName") ?? string.Empty;
            var logicalAddress = GetDirectAttributeValue(tag, "LogicalAddress") ?? string.Empty;
            rows.Add(new TagTableRow(
                $"tag:{tagTableName}:{tagName}:{logicalAddress}",
                tagTableName,
                tagTableSourcePath,
                tagName,
                NormalizeDataType(rawDataType),
                rawDataType,
                logicalAddress,
                ParseNullableBool(GetDirectAttributeValue(tag, "ExternalAccessible")),
                ParseNullableBool(GetDirectAttributeValue(tag, "ExternalVisible")),
                ParseNullableBool(GetDirectAttributeValue(tag, "ExternalWritable")),
                GetPreferredComment(tag),
                sourceFile));
        }

        return rows;
    }

    private static string? GetDirectAttributeValue(XElement element, string name)
    {
        return element
            .Elements()
            .FirstOrDefault(child => child.Name.LocalName == "AttributeList")
            ?.Elements()
            .FirstOrDefault(child => child.Name.LocalName == name)
            ?.Value
            .Trim();
    }

    private static string NormalizeDataType(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"'
            ? trimmed.Substring(1, trimmed.Length - 2)
            : trimmed;
    }

    private static bool? ParseNullableBool(string? value)
    {
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string GetPreferredComment(XElement tag)
    {
        var comment = tag
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "MultilingualText" &&
                string.Equals((string?)element.Attribute("CompositionName"), "Comment", StringComparison.OrdinalIgnoreCase));

        if (comment == null)
        {
            return string.Empty;
        }

        var items = comment
            .Descendants()
            .Where(element => element.Name.LocalName == "MultilingualTextItem")
            .Select(item => new
            {
                Culture = GetDirectAttributeValue(item, "Culture") ?? string.Empty,
                Text = GetDirectAttributeValue(item, "Text") ?? string.Empty
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .ToArray();

        return items.FirstOrDefault(item => string.Equals(item.Culture, "en-GB", StringComparison.OrdinalIgnoreCase))?.Text
            ?? items.FirstOrDefault(item => string.Equals(item.Culture, "en-US", StringComparison.OrdinalIgnoreCase))?.Text
            ?? items.FirstOrDefault()?.Text
            ?? string.Empty;
    }
}
