using System;
using System.IO;
using Contracts.Sandbox;
using Xunit;

namespace Contracts.Tests;

public sealed class PathJailTests : IDisposable
{
    private readonly string root;
    private readonly PathJail jail;

    public PathJailTests()
    {
        root = Path.Combine(Path.GetTempPath(), "sandbox-jail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        jail = new PathJail(new[] { root });
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void PathInsideRootPassesAndCanonicalizes()
    {
        var messy = Path.Combine(root, "sub", "..", "file.xml");
        var validated = jail.Validate(messy, "outputDir");
        Assert.Equal(Path.Combine(root, "file.xml"), validated);
    }

    [Fact]
    public void RootItselfPasses()
    {
        Assert.Null(Record.Exception(() => jail.Validate(root, "outputDir")));
    }

    [Fact]
    public void PathOutsideRootIsDenied()
    {
        var outside = Path.Combine(Path.GetTempPath(), "somewhere-else", "file.xml");
        var ex = Assert.Throws<SandboxException>(() => jail.Validate(outside, "outputDir"));
        Assert.Equal("SANDBOX_PATH_DENIED", ex.Code);
        Assert.Contains("outputDir", ex.Message);
    }

    [Fact]
    public void SiblingWithSharedPrefixIsDenied()
    {
        // C:\temp\root must not allow C:\temp\root-evil (prefix attack without separator).
        var evil = root + "-evil";
        Directory.CreateDirectory(evil);
        try
        {
            Assert.Throws<SandboxException>(() => jail.Validate(Path.Combine(evil, "f.xml"), "outputDir"));
        }
        finally
        {
            Directory.Delete(evil);
        }
    }

    [Fact]
    public void UncPathIsDenied()
    {
        var ex = Assert.Throws<SandboxException>(() => jail.Validate(@"\\server\share\file.xml", "projectPath"));
        Assert.Equal("SANDBOX_PATH_DENIED", ex.Code);
        Assert.Contains("network", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyPathIsDenied()
    {
        Assert.Throws<SandboxException>(() => jail.Validate("  ", "outputDir"));
    }

    [Fact]
    public void TraversalEscapingTheRootIsDenied()
    {
        var escaped = Path.Combine(root, "..", "escape.xml");
        Assert.Throws<SandboxException>(() => jail.Validate(escaped, "xmlFilePath"));
    }
}
