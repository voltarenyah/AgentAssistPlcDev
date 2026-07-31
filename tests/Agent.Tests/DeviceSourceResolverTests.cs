using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class DeviceSourceResolverTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "device-source-resolver-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void PrepareEditableCopiesBaselineAndMakesOverlayEffective()
    {
        var context = CreateContext();
        var baselinePath = Write(
            Path.Combine(context.ExportedSourceRoot, "Blocks", "Main.xml"),
            "baseline");
        var modifiedPath = Path.Combine(
            context.ModifiedSourceRoot,
            "Blocks",
            "Main.xml");
        var staleNotifications = new List<DeviceContext>();
        var resolver = new DeviceSourceResolver(staleNotifications.Add);

        Assert.Equal(
            baselinePath,
            resolver.ResolveEffective(context, "Blocks/Main.xml"));

        var editable = resolver.PrepareEditable(context, "Blocks/Main.xml");

        Assert.Equal(modifiedPath, editable);
        Assert.Equal(
            File.ReadAllBytes(baselinePath),
            File.ReadAllBytes(modifiedPath));
        Assert.Equal(
            modifiedPath,
            resolver.ResolveEffective(context, "Blocks/Main.xml"));
        Assert.Equal(context, Assert.Single(staleNotifications));
    }

    [Fact]
    public void PrepareEditablePreservesExistingOverlayAndStillMarksEditIntentStale()
    {
        var context = CreateContext();
        Write(
            Path.Combine(context.ExportedSourceRoot, "Blocks", "Main.xml"),
            "new baseline");
        var modifiedPath = Write(
            Path.Combine(context.ModifiedSourceRoot, "Blocks", "Main.xml"),
            "existing edit");
        var staleNotifications = 0;
        var resolver = new DeviceSourceResolver(_ => staleNotifications++);

        var editable = resolver.PrepareEditable(context, "Blocks/Main.xml");

        Assert.Equal(modifiedPath, editable);
        Assert.Equal("existing edit", File.ReadAllText(modifiedPath));
        Assert.Equal(1, staleNotifications);
    }

    [Fact]
    public void MetadataCallbackCanPersistKnowledgeAsStale()
    {
        var context = CreateContext();
        Write(
            Path.Combine(context.ExportedSourceRoot, "Blocks", "Main.xml"),
            "baseline");
        var metadata = new DeviceMetadata(
            WorkbenchSchema.CurrentVersion,
            context.DeviceId,
            context.WorktreeId,
            "PLC_1",
            "engineering-1",
            null,
            null,
            null,
            new KnowledgeState(
                false,
                new Dictionary<string, string>(),
                "2026-07-27T00:00:00.0000000Z"),
            Array.Empty<DeviceImportRecord>());
        var resolver = new DeviceSourceResolver(_ =>
            metadata = metadata with
            {
                Knowledge = metadata.Knowledge with { Stale = true },
            });

        resolver.PrepareEditable(context, "Blocks/Main.xml");

        Assert.True(metadata.Knowledge.Stale);
    }

    [Fact]
    public void EnumerateModifiedReturnsOnlyNormalizedSparseOverlayPaths()
    {
        var context = CreateContext();
        Write(Path.Combine(context.ModifiedSourceRoot, "Blocks", "B.xml"), "b");
        Write(Path.Combine(context.ModifiedSourceRoot, "Blocks", "Nested", "A.xml"), "a");
        var resolver = new DeviceSourceResolver(_ => { });

        var paths = resolver.EnumerateModified(context);

        Assert.Equal(
            new[] { "Blocks/B.xml", "Blocks/Nested/A.xml" },
            paths);
    }

    [Fact]
    public void EnumerateModifiedSkipsDisposablePreviewFiles()
    {
        var context = CreateContext();
        Write(Path.Combine(context.ModifiedSourceRoot, "Blocks", "Main.xml"), "overlay");
        Write(Path.Combine(context.ModifiedSourceRoot, "Blocks", "Main.preview.xml"), "preview");
        var resolver = new DeviceSourceResolver(_ => { });

        Assert.Equal(new[] { "Blocks/Main.xml" }, resolver.EnumerateModified(context));
    }

    [Theory]
    [InlineData("../escape.xml")]
    [InlineData("Blocks/../../escape.xml")]
    public void RelativePathTraversalIsRejected(string relativePath)
    {
        var context = CreateContext();
        var resolver = new DeviceSourceResolver(_ => { });

        Assert.Throws<WorkbenchPathException>(() =>
            resolver.PrepareEditable(context, relativePath));
    }

    [Fact]
    public void RootedPathsAreRejected()
    {
        var context = CreateContext();
        var resolver = new DeviceSourceResolver(_ => { });
        var rootedPath = Path.Combine(root, "escape.xml");

        Assert.Throws<WorkbenchPathException>(() =>
            resolver.ResolveEffective(context, rootedPath));
    }

    [Fact]
    public void MissingBaselineCannotCreateAnOverlay()
    {
        var context = CreateContext();
        var resolver = new DeviceSourceResolver(_ => { });
        var newPath = Path.Combine(
            context.ModifiedSourceRoot,
            "Blocks",
            "New.xml");

        Assert.Throws<FileNotFoundException>(() =>
            resolver.PrepareEditable(context, "Blocks/New.xml"));
        Assert.False(File.Exists(newPath));
        Assert.False(File.Exists(
            Path.Combine(context.ExportedSourceRoot, "Blocks", "New.xml")));
    }

    [Fact]
    public void CreateNewCreatesOnlyAnOverlayAndMarksKnowledgeStale()
    {
        var context = CreateContext();
        var initialContent = new byte[] { 0, 1, 2, 127, 255 };
        var staleNotifications = 0;
        var resolver = new DeviceSourceResolver(_ => staleNotifications++);
        var modifiedPath = Path.Combine(
            context.ModifiedSourceRoot,
            "Blocks",
            "Authored.xml");
        var baselinePath = Path.Combine(
            context.ExportedSourceRoot,
            "Blocks",
            "Authored.xml");

        var created = resolver.CreateNew(
            context,
            "Blocks/Authored.xml",
            initialContent);

        Assert.Equal(modifiedPath, created);
        Assert.Equal(initialContent, File.ReadAllBytes(modifiedPath));
        Assert.False(File.Exists(baselinePath));
        Assert.Equal(modifiedPath, resolver.ResolveEffective(
            context,
            "Blocks/Authored.xml"));
        Assert.Equal(1, staleNotifications);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(
                Path.GetDirectoryName(modifiedPath)!,
                "*",
                SearchOption.TopDirectoryOnly),
            path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateNewRefusesToOverwriteAndDoesNotMarkKnowledgeStale()
    {
        var context = CreateContext();
        var modifiedPath = Write(
            Path.Combine(
                context.ModifiedSourceRoot,
                "Blocks",
                "Authored.xml"),
            "keep");
        var staleNotifications = 0;
        var resolver = new DeviceSourceResolver(_ => staleNotifications++);

        Assert.Throws<IOException>(() =>
            resolver.CreateNew(
                context,
                "Blocks/Authored.xml",
                new byte[] { 1, 2, 3 }));

        Assert.Equal("keep", File.ReadAllText(modifiedPath));
        Assert.Equal(0, staleNotifications);
    }

    [Fact]
    public void CreateNewRejectsTraversalWithoutMarkingKnowledgeStale()
    {
        var context = CreateContext();
        var staleNotifications = 0;
        var resolver = new DeviceSourceResolver(_ => staleNotifications++);

        Assert.Throws<WorkbenchPathException>(() =>
            resolver.CreateNew(
                context,
                "../escape.xml",
                new byte[] { 1 }));

        Assert.False(File.Exists(Path.Combine(context.DeviceRoot, "escape.xml")));
        Assert.Equal(0, staleNotifications);
    }

    [Fact]
    public void CreateNewRejectsRootedPathWithoutMarkingKnowledgeStale()
    {
        var context = CreateContext();
        var staleNotifications = 0;
        var resolver = new DeviceSourceResolver(_ => staleNotifications++);
        var rootedPath = Path.Combine(root, "escape.xml");

        Assert.Throws<WorkbenchPathException>(() =>
            resolver.CreateNew(
                context,
                rootedPath,
                new byte[] { 1 }));

        Assert.False(File.Exists(rootedPath));
        Assert.Equal(0, staleNotifications);
    }

    [Fact]
    public void CreateNewRejectsReparsePointEscapeWhenSupported()
    {
        var context = CreateContext();
        Directory.CreateDirectory(context.ModifiedSourceRoot);
        var outside = Path.Combine(root, "outside-new");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(context.ModifiedSourceRoot, "Blocks");

        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        var staleNotifications = 0;
        var resolver = new DeviceSourceResolver(_ => staleNotifications++);

        Assert.Throws<WorkbenchPathException>(() =>
            resolver.CreateNew(
                context,
                "Blocks/Authored.xml",
                new byte[] { 1 }));
        Assert.False(File.Exists(Path.Combine(outside, "Authored.xml")));
        Assert.Equal(0, staleNotifications);
    }

    [Fact]
    public void ReparsePointEscapeIsRejectedWhenSupported()
    {
        var context = CreateContext();
        Directory.CreateDirectory(context.ExportedSourceRoot);
        Directory.CreateDirectory(context.ModifiedSourceRoot);
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        Write(Path.Combine(outside, "Main.xml"), "outside");
        var link = Path.Combine(context.ExportedSourceRoot, "Blocks");

        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        var resolver = new DeviceSourceResolver(_ => { });

        Assert.Throws<WorkbenchPathException>(() =>
            resolver.PrepareEditable(context, "Blocks/Main.xml"));
        Assert.False(File.Exists(
            Path.Combine(context.ModifiedSourceRoot, "Blocks", "Main.xml")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private DeviceContext CreateContext()
    {
        var worktreeRoot = Path.Combine(root, "worktree");
        var deviceRoot = Path.Combine(worktreeRoot, "devices", "PLC_1");
        return new DeviceContext(
            "wb-1",
            "wt-1",
            "dev-1",
            root,
            worktreeRoot,
            deviceRoot,
            Path.Combine(deviceRoot, "exported-source"),
            Path.Combine(deviceRoot, "modified-source"),
            Path.Combine(deviceRoot, "staging"),
            Path.Combine(deviceRoot, "plc-knowledge.db"));
    }

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
