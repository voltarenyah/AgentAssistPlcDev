using System.Text.RegularExpressions;
using Mcp.Engineering.Export;
using Xunit;

namespace Mcp.Engineering.Tests;

/// <summary>
/// <see cref="ContentHasher"/> must be insensitive to export-volatile bytes (the &lt;Created&gt;
/// line, line endings — same normalization contract as XmlCompare) and sensitive to real content.
/// </summary>
public sealed class ContentHasherTests
{
    private const string XmlA = "<Document>\n  <Created>2026-07-18T08:00:00</Created>\n  <SW.Blocks.FB ID=\"0\"><AttributeList><Name>A</Name></AttributeList></SW.Blocks.FB>\n</Document>";
    private const string XmlASameContentNewCreated = "<Document>\n  <Created>2026-07-20T11:11:11</Created>\n  <SW.Blocks.FB ID=\"0\"><AttributeList><Name>A</Name></AttributeList></SW.Blocks.FB>\n</Document>";
    private const string XmlB = "<Document>\n  <Created>2026-07-18T08:00:00</Created>\n  <SW.Blocks.FB ID=\"0\"><AttributeList><Name>B</Name></AttributeList></SW.Blocks.FB>\n</Document>";

    [Fact]
    public void CreatedLine_IsIgnored()
    {
        Assert.Equal(ContentHasher.Compute(XmlA), ContentHasher.Compute(XmlASameContentNewCreated));
    }

    [Fact]
    public void LineEndings_AreIgnored()
    {
        Assert.Equal(ContentHasher.Compute(XmlA), ContentHasher.Compute(XmlA.Replace("\n", "\r\n")));
    }

    [Fact]
    public void ContentChange_MovesTheHash()
    {
        Assert.NotEqual(ContentHasher.Compute(XmlA), ContentHasher.Compute(XmlB));
    }

    [Fact]
    public void Hash_IsBase64UrlSha256WithoutPadding()
    {
        var hash = ContentHasher.Compute(XmlA);

        // 32 bytes → 43 base64url chars, '=' padding trimmed (same encoding as the stable id).
        Assert.Equal(43, hash.Length);
        Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), hash);
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        Assert.Equal(ContentHasher.Compute(XmlA), ContentHasher.Compute(XmlA));
    }
}
