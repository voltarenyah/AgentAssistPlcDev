using System;
using System.IO;
using System.Threading.Tasks;
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

    [Fact]
    public void RegisteredTrustedRootIsReloadedAndUnregisteredRootRemainsDenied()
    {
        var registryPath = Path.Combine(root, "trusted-roots.json");
        var customRoot = Path.Combine(Path.GetTempPath(), "custom-workbench-" + Guid.NewGuid().ToString("N"));
        var arbitraryRoot = Path.Combine(Path.GetTempPath(), "arbitrary-workbench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(customRoot);
        Directory.CreateDirectory(arbitraryRoot);
        try
        {
            var dynamicJail = new PathJail(new[] { root }, registryPath);
            Assert.Throws<SandboxException>(() =>
                dynamicJail.Validate(Path.Combine(customRoot, "source.xml"), "xmlFilePath"));

            WriteWorkbenchMetadata(customRoot, "wb-custom");
            new TrustedWorkbenchRootRegistry(registryPath).Register("wb-custom", customRoot);

            Assert.Equal(
                Path.Combine(customRoot, "source.xml"),
                dynamicJail.Validate(Path.Combine(customRoot, "source.xml"), "xmlFilePath"));
            Assert.Throws<SandboxException>(() =>
                dynamicJail.Validate(Path.Combine(arbitraryRoot, "source.xml"), "xmlFilePath"));
        }
        finally
        {
            Directory.Delete(customRoot, true);
            Directory.Delete(arbitraryRoot, true);
        }
    }

    [Fact]
    public void DeletingOrTamperingWorkbenchMetadataRevokesGrantImmediately()
    {
        var registryPath = Path.Combine(root, "trusted-roots.json");
        var customRoot = Path.Combine(root, "custom-workbench");
        Directory.CreateDirectory(customRoot);
        WriteWorkbenchMetadata(customRoot, "wb-1");
        var registry = new TrustedWorkbenchRootRegistry(registryPath);
        registry.Register("wb-1", customRoot);
        var dynamicJail = new PathJail(Array.Empty<string>(), registryPath);
        var source = Path.Combine(customRoot, "source.xml");
        Assert.Equal(source, dynamicJail.Validate(source, "xmlFilePath"));

        File.Delete(Path.Combine(customRoot, "workbench.json"));
        Assert.Throws<SandboxException>(() => dynamicJail.Validate(source, "xmlFilePath"));

        WriteWorkbenchMetadata(customRoot, "wrong-id");
        Assert.Throws<SandboxException>(() => dynamicJail.Validate(source, "xmlFilePath"));

        Directory.Delete(customRoot, true);
        Directory.CreateDirectory(customRoot);
        Assert.Throws<SandboxException>(() => dynamicJail.Validate(source, "xmlFilePath"));
    }

    [Fact]
    public void ReconcileAtomicallyReplacesMultipleRootGrantsAndMalformedRegistryFailsClosed()
    {
        var registryPath = Path.Combine(root, "trusted-roots.json");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        WriteWorkbenchMetadata(first, "wb-1");
        WriteWorkbenchMetadata(second, "wb-2");
        var registry = new TrustedWorkbenchRootRegistry(registryPath);

        registry.Reconcile(new[]
        {
            new TrustedWorkbenchRoot("wb-1", first),
            new TrustedWorkbenchRoot("wb-2", second),
        });
        Assert.Equal(2, registry.Read().Count);

        File.Delete(Path.Combine(first, "workbench.json"));
        registry.Reconcile(new[] { new TrustedWorkbenchRoot("wb-2", second) });
        Assert.DoesNotContain(first, registry.Read(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(second, registry.Read(), StringComparer.OrdinalIgnoreCase);

        File.WriteAllText(registryPath, "{ malformed");
        Assert.Empty(registry.Read());
        Assert.Throws<SandboxException>(() =>
            new PathJail(Array.Empty<string>(), registryPath)
                .Validate(Path.Combine(second, "source.xml"), "xmlFilePath"));
    }

    [Fact]
    public void ConcurrentHostInstancesPreserveEachOthersValidRoots()
    {
        var registryPath = Path.Combine(root, "trusted-roots.json");
        var first = Path.Combine(root, "host-one");
        var second = Path.Combine(root, "host-two");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        WriteWorkbenchMetadata(first, "wb-1");
        WriteWorkbenchMetadata(second, "wb-2");
        var firstHost = new TrustedWorkbenchRootRegistry(registryPath);
        var secondHost = new TrustedWorkbenchRootRegistry(registryPath);

        Parallel.Invoke(
            () => firstHost.Register("wb-1", first),
            () => secondHost.Register("wb-2", second));

        Assert.Contains(first, firstHost.Read(), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(second, firstHost.Read(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailedRegistryReplacementCleansTemporaryFile()
    {
        var registryPath = Path.Combine(root, "registry-blocker");
        Directory.CreateDirectory(registryPath);
        var customRoot = Path.Combine(root, "valid-workbench");
        Directory.CreateDirectory(customRoot);
        WriteWorkbenchMetadata(customRoot, "wb-1");
        var registry = new TrustedWorkbenchRootRegistry(registryPath);

        Assert.ThrowsAny<IOException>(() => registry.Register("wb-1", customRoot));
        Assert.Empty(Directory.EnumerateFiles(root, "registry-blocker.*.tmp"));
    }

    [Fact]
    public void RegisteredRootDoesNotAllowReparsePointEscape()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var customRoot = Path.Combine(root, "custom");
        var outside = Path.Combine(Path.GetTempPath(), "sandbox-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(customRoot);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(customRoot, "link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Directory.Delete(outside, true);
            return;
        }

        try
        {
            Assert.Throws<SandboxException>(() =>
                jail.Validate(Path.Combine(link, "source.xml"), "xmlFilePath"));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(outside, true);
        }
    }

    private static void WriteWorkbenchMetadata(string workbenchRoot, string workbenchId)
    {
        File.WriteAllText(
            Path.Combine(workbenchRoot, "workbench.json"),
            $$"""{"schemaVersion":"1.0","workbenchId":"{{workbenchId}}","rootPath":{{System.Text.Json.JsonSerializer.Serialize(Path.GetFullPath(workbenchRoot))}}}""");
    }
}
