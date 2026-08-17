using Contracts.Engineering;
using Xunit;

namespace Contracts.Tests;

public class XmlCompareTests
{
    [Fact]
    public void Normalize_RemovesCreatedTimestampLines()
    {
        var xml = "<Document>\n  <Created>2026-07-18T05:18:21Z</Created>\n  <Body />\n</Document>";
        Assert.Equal("<Document>\n  <Body />\n</Document>", XmlCompare.Normalize(xml));
    }

    [Fact]
    public void Normalize_NormalizesCrLf()
    {
        Assert.Equal("a\nb", XmlCompare.Normalize("a\r\nb"));
    }

    [Fact]
    public void Normalize_IdenticalExceptTimestamp_ComparesEqual()
    {
        var a = "<Document>\n  <Created>t1</Created>\n  <Text>hi</Text>\n</Document>";
        var b = "<Document>\n  <Created>t2</Created>\n  <Text>hi</Text>\n</Document>";
        Assert.Equal(XmlCompare.Normalize(a), XmlCompare.Normalize(b));
    }

    [Fact]
    public void Normalize_IgnoresCaxLastWritingDateTime()
    {
        var a = "<CAEXFile>\n  <WriterHeader>\n    <LastWritingDateTime>2026-08-16T15:00:00Z</LastWritingDateTime>\n  </WriterHeader>\n  <InstanceHierarchy />\n</CAEXFile>";
        var b = "<CAEXFile>\n  <WriterHeader>\n    <LastWritingDateTime>2026-08-17T15:00:00Z</LastWritingDateTime>\n  </WriterHeader>\n  <InstanceHierarchy />\n</CAEXFile>";

        Assert.Equal(XmlCompare.Normalize(a), XmlCompare.Normalize(b));
        Assert.Equal(XmlContentHash.Compute(a), XmlContentHash.Compute(b));
    }

    [Fact]
    public void Normalize_DifferentContent_ComparesUnequal()
    {
        var a = "<Document>\n  <Text>hi</Text>\n</Document>";
        var b = "<Document>\n  <Text>bye</Text>\n</Document>";
        Assert.NotEqual(XmlCompare.Normalize(a), XmlCompare.Normalize(b));
    }

    [Theory]
    [InlineData("<Created>one</Created>", "<Created>two</Created>")]
    [InlineData("  <Created>one</Created>\r\n<X />", "  <Created>two</Created>\n<X />")]
    public void TimestampAndLineEndingDifferencesHaveTheSameFingerprint(string left, string right)
    {
        Assert.Equal(XmlContentHash.Compute(left), XmlContentHash.Compute(right));
    }
}
