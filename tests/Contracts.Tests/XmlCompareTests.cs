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
    public void Normalize_DifferentContent_ComparesUnequal()
    {
        var a = "<Document>\n  <Text>hi</Text>\n</Document>";
        var b = "<Document>\n  <Text>bye</Text>\n</Document>";
        Assert.NotEqual(XmlCompare.Normalize(a), XmlCompare.Normalize(b));
    }
}
