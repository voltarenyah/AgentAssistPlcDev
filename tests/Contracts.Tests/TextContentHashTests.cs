using System;
using System.IO;
using Contracts.Engineering;
using Xunit;

namespace Contracts.Tests;

public sealed class TextContentHashTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"text-content-hash-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ComputeMatchesSha256Base64UrlConvention()
    {
        // SHA-256 of the empty string, base64url without padding (same convention as XmlContentHash).
        Assert.Equal("47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU", TextContentHash.Compute(string.Empty));
    }

    [Fact]
    public void ComputeIsDeterministicAndSensitiveToContent()
    {
        var text = "subnet|PN/IE_1|Ethernet|-\n";

        Assert.Equal(TextContentHash.Compute(text), TextContentHash.Compute(text));
        Assert.NotEqual(TextContentHash.Compute(text), TextContentHash.Compute(text + "x"));
    }

    [Fact]
    public void TryComputeFileRoundTripsWrittenText()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "network-configuration.txt");
        File.WriteAllText(path, "interface|PLC_1|PN_1|Ethernet|None\n");

        Assert.Equal(
            TextContentHash.Compute(File.ReadAllText(path)),
            TextContentHash.TryComputeFile(path));
    }

    [Fact]
    public void TryComputeFileReturnsNullForMissingFile()
    {
        Assert.Null(TextContentHash.TryComputeFile(Path.Combine(root, "missing.txt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
