using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Contracts.Engineering;
using Xunit;

namespace Contracts.Tests;

public class PlcXmlChangeSummaryTests
{
    [Fact]
    public void Compare_ReportsSafeHeaderChangesWithoutLogicChange()
    {
        var before = SiemensXml(headerAuthor: "Before", headerFamily: "Family A", headerName: "Header A");
        var after = SiemensXml(headerAuthor: "After", headerFamily: "Family B", headerName: "Header B");

        var summary = PlcXmlChangeSummary.Compare(before, after);

        Assert.True(summary.SummaryAvailable);
        Assert.False(summary.LogicOrStructureChanged);
        Assert.Collection(summary.HeaderChanges,
            change => Assert.Equal(new PlcXmlHeaderChange("HeaderAuthor", "Before", "After"), change),
            change => Assert.Equal(new PlcXmlHeaderChange("HeaderFamily", "Family A", "Family B"), change),
            change => Assert.Equal(new PlcXmlHeaderChange("HeaderName", "Header A", "Header B"), change));
        Assert.Empty(summary.MultilingualTextChanges);
    }

    [Theory]
    [InlineData("HeaderAuthor", "Author", "Old<Protected Flag=\"1\" />", "New<Protected Flag=\"2\" />")]
    [InlineData("HeaderFamily", "Family", "<![CDATA[Old family]]>", "<![CDATA[New family]]>")]
    [InlineData("HeaderName", "Header", "Old<!--protected-old-->", "New<!--protected-new-->")]
    public void Compare_NestedOrAmbiguousSafeHeaderIsNeverMasked(
        string field,
        string originalValue,
        string beforeContent,
        string afterContent)
    {
        var original = $"<{field}>{originalValue}</{field}>";
        var before = SiemensXml().Replace(original, $"<{field}>{beforeContent}</{field}>");
        var after = SiemensXml().Replace(original, $"<{field}>{afterContent}</{field}>");

        var summary = PlcXmlChangeSummary.Compare(before, after);

        Assert.False(summary.SummaryAvailable);
        Assert.False(summary.LogicOrStructureChanged);
        Assert.Empty(summary.HeaderChanges);
    }

    [Fact]
    public void Compare_ReportsMultilingualChangesInDeterministicOwnerFieldCultureOrder()
    {
        var before = SiemensXml(
            blockTitle: [("zh-CN", "旧标题"), ("en-US", "Old block")],
            networkTitle: [("en-US", "Old network")],
            networkComment: [("de-DE", "Alt")]);
        var after = SiemensXml(
            blockTitle: [("zh-CN", "新标题"), ("en-US", "New block")],
            networkTitle: [("en-US", "New network")],
            networkComment: [("de-DE", "Neu")]);

        var summary = PlcXmlChangeSummary.Compare(before, after);

        Assert.True(summary.SummaryAvailable);
        Assert.False(summary.LogicOrStructureChanged);
        Assert.Equal(
        [
            new PlcXmlMultilingualTextChange("block", "B1", null, "Title", "en-US", "Old block", "New block"),
            new PlcXmlMultilingualTextChange("block", "B1", null, "Title", "zh-CN", "旧标题", "新标题"),
            new PlcXmlMultilingualTextChange("network", "N1", 1, "Comment", "de-DE", "Alt", "Neu"),
            new PlcXmlMultilingualTextChange("network", "N1", 1, "Title", "en-US", "Old network", "New network")
        ], summary.MultilingualTextChanges);
        Assert.Empty(summary.HeaderChanges);
    }

    [Fact]
    public void Compare_ReportsLogicOrStructureChange()
    {
        var before = SiemensXml(logic: "A := B;");
        var after = SiemensXml(logic: "A := C;");

        var summary = PlcXmlChangeSummary.Compare(before, after);

        Assert.True(summary.SummaryAvailable);
        Assert.True(summary.LogicOrStructureChanged);
        Assert.Empty(summary.HeaderChanges);
        Assert.Empty(summary.MultilingualTextChanges);
    }

    [Fact]
    public void Compare_IgnoresCreatedFormattingLineEndingsAndAttributeOrder()
    {
        var before = SiemensXml(created: "2026-01-01", rootAttributes: "SchemaVersion=\"V1\" Exporter=\"TIA\"");
        var after = SiemensXml(created: "2026-02-02", rootAttributes: "Exporter=\"TIA\" SchemaVersion=\"V1\"")
            .Replace("><", ">\r\n    <");

        var summary = PlcXmlChangeSummary.Compare(before, after);

        Assert.True(summary.SummaryAvailable);
        Assert.False(summary.LogicOrStructureChanged);
        Assert.Empty(summary.HeaderChanges);
        Assert.Empty(summary.MultilingualTextChanges);
    }

    [Fact]
    public void Compare_RemovedOrCultureMutatedMultilingualValueIsAlsoStructureChange()
    {
        var before = SiemensXml(blockTitle: [("en-US", "English"), ("de-DE", "Deutsch")]);
        var after = SiemensXml(blockTitle: [("en-US", "English"), ("zh-CN", "中文")]);

        var summary = PlcXmlChangeSummary.Compare(before, after);

        Assert.True(summary.SummaryAvailable);
        Assert.True(summary.LogicOrStructureChanged);
        Assert.Equal(
        [
            new PlcXmlMultilingualTextChange("block", "B1", null, "Title", "de-DE", "Deutsch", null),
            new PlcXmlMultilingualTextChange("block", "B1", null, "Title", "zh-CN", null, "中文")
        ], summary.MultilingualTextChanges);
    }

    [Fact]
    public void Compare_AddedCultureWithApprovedExistingItemShapeIsTextOnlyChange()
    {
        var before = SiemensXml(blockTitle: [("en-US", "English")]);
        var after = SiemensXml(blockTitle: [("en-US", "English"), ("zh-CN", "中文")]);

        var summary = PlcXmlChangeSummary.Compare(before, after);

        Assert.True(summary.SummaryAvailable);
        Assert.False(summary.LogicOrStructureChanged);
        Assert.Equal(
            [new PlcXmlMultilingualTextChange("block", "B1", null, "Title", "zh-CN", null, "中文")],
            summary.MultilingualTextChanges);
    }

    [Theory]
    [InlineData("MultilingualText ID=\"BT\"", "MultilingualText ID=\"CHANGED\"")]
    [InlineData("MultilingualTextItem ID=\"BT0\"", "MultilingualTextItem ID=\"CHANGED\"")]
    [InlineData("<Text>Block title</Text>", "<Text>Changed title</Text><Protected Flag=\"1\" />")]
    public void Compare_MultilingualShellMutationRemainsProtected(string oldFragment, string newFragment)
    {
        var before = SiemensXml();
        var after = before.Replace(oldFragment, newFragment);

        var summary = PlcXmlChangeSummary.Compare(before, after);

        Assert.True(summary.SummaryAvailable);
        Assert.True(summary.LogicOrStructureChanged);
    }

    [Theory]
    [InlineData("SW.Blocks.OB")]
    [InlineData("SW.Blocks.FB")]
    [InlineData("SW.Blocks.FC")]
    [InlineData("SW.Blocks.DB")]
    [InlineData("SW.Blocks.GlobalDB")]
    [InlineData("SW.Blocks.InstanceDB")]
    [InlineData("SW.Blocks.ArrayDB")]
    public void Compare_RecognizesAllSupportedBlockRoots(string objectType)
    {
        var xml = SiemensXml(objectType: objectType);

        var summary = PlcXmlChangeSummary.Compare(xml, xml);

        Assert.True(summary.SummaryAvailable);
        Assert.False(summary.LogicOrStructureChanged);
    }

    [Fact]
    public void Compare_RecognizesRealUdtAndSummarizesItsText()
    {
        var before = ReadFixture("CAB.xml");
        var after = ReplaceFirst(before, "<Text />", "<Text>Changed UDT comment</Text>");
        var structural = before.Replace("String[20]", "String[21]");

        var summary = PlcXmlChangeSummary.Compare(before, after);
        var structuralSummary = PlcXmlChangeSummary.Compare(before, structural);

        Assert.True(summary.SummaryAvailable);
        Assert.False(summary.LogicOrStructureChanged);
        var change = Assert.Single(summary.MultilingualTextChanges);
        Assert.Equal("udt", change.OwnerKind);
        Assert.Equal("0", change.OwnerId);
        Assert.Equal("Comment", change.Field);
        Assert.Equal("fr-FR", change.Culture);
        Assert.True(structuralSummary.SummaryAvailable);
        Assert.True(structuralSummary.LogicOrStructureChanged);
    }

    [Fact]
    public void Compare_RecognizesRealTagTableAndProtectsTagStructure()
    {
        var before = ReadFixture("IO_CC_Cav_A.xml");
        var textOnly = ReplaceFirst(before, "<Text />", "<Text>Changed tag comment</Text>");
        var structural = before.Replace("%Q600.7", "%Q601.0");

        var textSummary = PlcXmlChangeSummary.Compare(before, textOnly);
        var structuralSummary = PlcXmlChangeSummary.Compare(before, structural);

        Assert.True(textSummary.SummaryAvailable);
        Assert.False(textSummary.LogicOrStructureChanged);
        var change = Assert.Single(textSummary.MultilingualTextChanges);
        Assert.Equal("tag", change.OwnerKind);
        Assert.Equal("1", change.OwnerId);
        Assert.Equal("Comment", change.Field);
        Assert.True(structuralSummary.SummaryAvailable);
        Assert.True(structuralSummary.LogicOrStructureChanged);
    }

    [Fact]
    public void Compare_NetworkReorderUsesStableOwnerIdentityNotDisplayNumber()
    {
        var first = CompileUnit("N1", "First", "A := B;");
        var second = CompileUnit("N2", "Second", "C := D;");
        var before = SiemensXml(networkUnits: first + second);
        var after = SiemensXml(networkUnits: second + first.Replace("First", "First changed"));

        var summary = PlcXmlChangeSummary.Compare(before, after);

        Assert.True(summary.SummaryAvailable);
        Assert.True(summary.LogicOrStructureChanged);
        var change = Assert.Single(summary.MultilingualTextChanges);
        Assert.Equal("N1", change.OwnerId);
        Assert.Equal(2, change.NetworkNumber);
        Assert.Equal("First", change.OldValue);
        Assert.Equal("First changed", change.NewValue);
    }

    [Fact]
    public void Compare_IgnoresOnlyDocumentInfoCreatedMetadata()
    {
        var before = SiemensXml(created: "2026-01-01", protectedCreated: "logic-v1");
        var metadataOnly = SiemensXml(created: "2026-02-02", protectedCreated: "logic-v1");
        var protectedChange = SiemensXml(created: "2026-02-02", protectedCreated: "logic-v2");

        Assert.False(PlcXmlChangeSummary.Compare(before, metadataOnly).LogicOrStructureChanged);
        Assert.True(PlcXmlChangeSummary.Compare(before, protectedChange).LogicOrStructureChanged);
    }

    [Theory]
    [InlineData("<Text>Block title</Text>", "<Text>Old<Protected Flag=\"1\" /></Text>", "<Text>New<Protected Flag=\"2\" /></Text>")]
    [InlineData("<Text>Block title</Text>", "<Text>Old<!--protected-old--></Text>", "<Text>New<!--protected-new--></Text>")]
    public void Compare_NestedOrAmbiguousMultilingualTextIsNeverMasked(
        string original,
        string beforeText,
        string afterText)
    {
        var before = SiemensXml().Replace(original, beforeText);
        var after = SiemensXml().Replace(original, afterText);

        var summary = PlcXmlChangeSummary.Compare(before, after);

        Assert.False(summary.SummaryAvailable);
        Assert.False(summary.LogicOrStructureChanged);
        Assert.Empty(summary.MultilingualTextChanges);
    }

    [Theory]
    [InlineData("<Document>", "<Document />")]
    [InlineData("<Document />", "not xml")]
    [InlineData("<Document />", "<Document />")]
    public void Compare_WhenEitherSideIsMalformedOrUnsupported_ReturnsUnavailable(string before, string after)
    {
        var summary = PlcXmlChangeSummary.Compare(before, after);

        Assert.False(summary.SummaryAvailable);
        Assert.False(summary.LogicOrStructureChanged);
        Assert.Empty(summary.HeaderChanges);
        Assert.Empty(summary.MultilingualTextChanges);
    }

    private static string SiemensXml(
        string headerAuthor = "Author",
        string headerFamily = "Family",
        string headerName = "Header",
        IReadOnlyList<(string Culture, string Text)>? blockTitle = null,
        IReadOnlyList<(string Culture, string Text)>? networkTitle = null,
        IReadOnlyList<(string Culture, string Text)>? networkComment = null,
        string logic = "A := B;",
        string created = "2026-01-01",
        string rootAttributes = "SchemaVersion=\"V1\" Exporter=\"TIA\"",
        string objectType = "SW.Blocks.OB",
        string protectedCreated = "logic-created",
        string? networkUnits = null)
    {
        blockTitle ??= [("en-US", "Block title")];
        networkTitle ??= [("en-US", "Network title")];
        networkComment ??= [("en-US", "Network comment")];

        networkUnits ??= CompileUnit("N1", networkTitle, logic, networkComment);
        return $"""
            <Document {rootAttributes}><Engineering version="1" /><DocumentInfo><Created>{created}</Created><ExportSetting>WithDefaults</ExportSetting></DocumentInfo><{objectType} ID="B1"><AttributeList><Name>Main</Name><HeaderAuthor>{headerAuthor}</HeaderAuthor><HeaderFamily>{headerFamily}</HeaderFamily><HeaderName>{headerName}</HeaderName></AttributeList><ObjectList>{Multilingual("Title", "BT", blockTitle)}<ProtectedMetadata><Created>{protectedCreated}</Created></ProtectedMetadata>{networkUnits}</ObjectList></{objectType}></Document>
            """;
    }

    private static string CompileUnit(string id, string title, string logic, string comment = "Network comment") =>
        CompileUnit(id, [("en-US", title)], logic, [("en-US", comment)]);

    private static string CompileUnit(
        string id,
        IReadOnlyList<(string Culture, string Text)> titles,
        string logic,
        IReadOnlyList<(string Culture, string Text)> comments) =>
        $"<SW.Blocks.CompileUnit ID=\"{id}\" CompositionName=\"CompileUnits\"><AttributeList><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList><ObjectList><StructuredText><Text>{logic}</Text></StructuredText>{Multilingual("Title", $"{id}T", titles)}{Multilingual("Comment", $"{id}C", comments)}</ObjectList></SW.Blocks.CompileUnit>";

    private static string Multilingual(
        string field,
        string id,
        IReadOnlyList<(string Culture, string Text)> values) =>
        $"<MultilingualText ID=\"{id}\" CompositionName=\"{field}\"><ObjectList>{string.Concat(values.Select((value, index) => $"<MultilingualTextItem ID=\"{id}{index}\" CompositionName=\"Items\"><AttributeList><Culture>{value.Culture}</Culture><Text>{value.Text}</Text></AttributeList></MultilingualTextItem>"))}</ObjectList></MultilingualText>";

    private static string ReadFixture(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(directory.FullName, "tests", "Mcp.Knowledge.Tests", "Fixtures", fileName);
            if (File.Exists(path)) return File.ReadAllText(path);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate Siemens fixture {fileName}.");
    }

    private static string ReplaceFirst(string value, string oldValue, string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Fixture fragment was not found: {oldValue}");
        return value.Substring(0, index) + newValue + value.Substring(index + oldValue.Length);
    }
}
