// Ported from PlcSourceExporter (src/PlcSourceExporter.Core/UdtTypeTable.cs) — adapted for mcp-knowledge; keep changes minimal to ease future re-syncs.
// Only the reader side ports: UdtTypeTableRow + UdtTypeTableBuilder.ParseRows. The export side
// (UdtTypeTableDocument, UdtExportedFile, Write, UdtTypeTableJsonSerializer) is mcp-engineering's job.
// Note: ParseRows intentionally flattens FIRST-LEVEL members only (reference behaviour — a nested
// struct member is a single row with its struct datatype; nested members are not expanded).
using System.Xml;
using System.Xml.Linq;

namespace Mcp.Knowledge.Parsing;

public sealed class UdtTypeTableRow
{
    public UdtTypeTableRow(
        string id,
        string kind,
        string parentType,
        string parentPath,
        string name,
        string path,
        string dataType,
        string sourcePath,
        string sourceFile)
    {
        Id = id;
        Kind = kind;
        ParentType = parentType;
        ParentPath = parentPath;
        Name = name;
        Path = path;
        DataType = dataType;
        SourcePath = sourcePath;
        SourceFile = sourceFile;
    }

    public string Id { get; }

    public string Kind { get; }

    public string ParentType { get; }

    public string ParentPath { get; }

    public string Name { get; }

    public string Path { get; }

    public string DataType { get; }

    public string SourcePath { get; }

    public string SourceFile { get; }
}

public static class UdtTypeTableBuilder
{
    public static IReadOnlyList<UdtTypeTableRow> ParseRows(string xml, string sourceFile, string sourcePath)
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
            return Array.Empty<UdtTypeTableRow>();
        }

        var plcStruct = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "SW.Types.PlcStruct");
        if (plcStruct == null)
        {
            return Array.Empty<UdtTypeTableRow>();
        }

        var attributeList = plcStruct.Elements().FirstOrDefault(element => element.Name.LocalName == "AttributeList");
        var rawTypeName = attributeList?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Name")
            ?.Value
            .Trim();

        var typeName = string.IsNullOrWhiteSpace(rawTypeName)
            ? Path.GetFileNameWithoutExtension(sourceFile) ?? string.Empty
            : rawTypeName!;

        var rows = new List<UdtTypeTableRow>
        {
            new UdtTypeTableRow(
                $"type:{typeName}",
                "Type",
                string.Empty,
                string.Empty,
                typeName,
                string.Empty,
                typeName,
                sourcePath,
                sourceFile)
        };

        var interfaceElement = attributeList?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Interface");
        var sectionsElement = interfaceElement?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName == "Sections");
        var sections = sectionsElement?
            .Elements()
            .Where(element => element.Name.LocalName == "Section")
            .ToArray() ?? Array.Empty<XElement>();

        foreach (var section in sections)
        {
            foreach (var member in section.Elements().Where(element => element.Name.LocalName == "Member"))
            {
                AddMemberRow(rows, member, typeName, sourcePath, sourceFile);
            }
        }

        return rows;
    }

    private static void AddMemberRow(
        List<UdtTypeTableRow> rows,
        XElement member,
        string typeName,
        string sourcePath,
        string sourceFile)
    {
        var rawName = member.Attribute("Name")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return;
        }

        var name = rawName!;
        var dataType = member.Attribute("Datatype")?.Value?.Trim() ?? string.Empty;
        rows.Add(new UdtTypeTableRow(
            $"member:{typeName}:{name}",
            "Member",
            typeName,
            string.Empty,
            name,
            name,
            dataType,
            sourcePath,
            sourceFile));
    }
}
