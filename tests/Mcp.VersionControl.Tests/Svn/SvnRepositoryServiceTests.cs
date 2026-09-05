using System;
using Mcp.VersionControl.Svn;
using Xunit;

namespace Mcp.VersionControl.Tests.Svn;

/// <summary>Offline tests for the SVN native store, using local file:// repositories in temp dirs.</summary>
public sealed class SvnRepositoryServiceTests : IDisposable
{
    private readonly string _root;
    private readonly SvnRepositoryService _svn = new();

    public SvnRepositoryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "McpVcSvnTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public void FormatSvnFailure_PreservesNestedExceptionDetails()
    {
        var nested = new IOException("The failing file is tia\\FOB_SAFETY.xml.");
        var failure = new InvalidOperationException("Commit failed (details follow):", nested);

        var details = SvnRepositoryService.FormatSvnFailure(failure);

        Assert.Contains("Commit failed (details follow):", details);
        Assert.Contains("The failing file is tia\\FOB_SAFETY.xml.", details);
    }

    private SvnSharedInitResult CreateShared()
    {
        var result = _svn.CreateShared(Path.Combine(_root, "workbench"));
        Assert.True(Directory.Exists(result.RepositoryPath));
        return result;
    }

    /// <summary>Create a fresh repository and commit a minimal native baseline (lands as r1).</summary>
    private SvnSharedInitResult CreateSharedWithMain()
    {
        var shared = CreateShared();
        var source = Path.Combine(_root, "baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "seed.txt"), "seed");
        var baseline = _svn.CommitNativeBaseline(shared.RepositoryUri, source, "native: seed baseline");
        Assert.Equal(1, baseline.Revision);
        return shared;
    }

    private string MainUrl(SvnSharedInitResult shared) => shared.RepositoryUri.TrimEnd('/') + "/native/main";

    [Fact]
    public void CreateShared_CreatesEmptyRepositoryWithoutScaffoldCommit()
    {
        var shared = CreateShared();

        Assert.True(shared.Initialized);
        Assert.StartsWith("file:///", shared.RepositoryUri);

        // No scaffolding commit: native/main does not exist until the native baseline is committed.
        var error = Assert.Throws<Git.VcInternalException>(
            () => _svn.Checkout(MainUrl(shared), Path.Combine(_root, "native-wc")));
        Assert.Equal("SVN_CHECKOUT_FAILED", error.Code);
    }

    [Fact]
    public void CreateShared_RejectsExistingNonEmptyRepository()
    {
        CreateShared();
        var error = Assert.Throws<Git.VcInternalException>(
            () => _svn.CreateShared(Path.Combine(_root, "workbench")));
        Assert.Equal("SVN_REPOSITORY_EXISTS", error.Code);
    }

    [Fact]
    public void CommitNativeBaseline_FreshRepository_LandsAsRevision1()
    {
        var shared = CreateShared();
        var source = Path.Combine(_root, "tia");
        Directory.CreateDirectory(Path.Combine(source, "IM"));
        File.WriteAllText(Path.Combine(source, "Line.ap17"), "project");
        File.WriteAllText(Path.Combine(source, "IM", "data.bin"), "data");

        var baseline = _svn.CommitNativeBaseline(
            shared.RepositoryUri, source, "native: initial managed TIA project baseline");

        Assert.True(baseline.Committed);
        Assert.Equal(1, baseline.Revision);

        // The source tree is back at its original path as a clean native/main working copy.
        Assert.True(_svn.Status(source).IsClean);
        var info = _svn.Info(source);
        Assert.Equal(MainUrl(shared), info.Uri);
        Assert.Equal(1, info.Revision);

        // The single commit created native/main together with the content — no scaffold commit.
        var log = _svn.Log(MainUrl(shared), allHistory: true);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(1, entry.Revision);
        Assert.Equal("native: initial managed TIA project baseline", entry.Message);

        // A fresh checkout round-trips the content.
        var roundTrip = Path.Combine(_root, "roundtrip");
        _svn.Checkout(MainUrl(shared), roundTrip);
        Assert.Equal("project", File.ReadAllText(Path.Combine(roundTrip, "Line.ap17")));
        Assert.Equal("data", File.ReadAllText(Path.Combine(roundTrip, "IM", "data.bin")));

        // The scratch working copy was cleaned up.
        Assert.Empty(Directory.GetDirectories(_root, ".svn-native-baseline-*"));
    }

    [Fact]
    public void CommitNativeBaseline_InvalidRepository_ThrowsAndLeavesTreeUntouched()
    {
        var source = Path.Combine(_root, "tia");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Line.ap17"), "project");
        var missingRepoUri = new Uri(Path.GetFullPath(Path.Combine(_root, "no-repo"))).ToString();

        var error = Assert.Throws<Git.VcInternalException>(
            () => _svn.CommitNativeBaseline(missingRepoUri, source, "baseline"));

        Assert.Equal("SVN_CHECKOUT_FAILED", error.Code);
        Assert.Equal("project", File.ReadAllText(Path.Combine(source, "Line.ap17")));
    }

    [Fact]
    public void CommitNativeBaseline_FinalCheckoutFailure_RestoresSourceTree()
    {
        var shared = CreateShared();
        var source = Path.Combine(_root, "tia");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "Line.ap17"), "project");

        SvnRepositoryService? service = null;
        var checkoutCalls = 0;
        service = new SvnRepositoryService((url, path, allowObstructions) =>
        {
            checkoutCalls++;
            if (checkoutCalls == 2)
            {
                throw new Git.VcInternalException(
                    "SVN_CHECKOUT_FAILED",
                    "simulated final checkout failure");
            }

            return service!.Checkout(url, path, allowObstructions);
        });

        var error = Assert.Throws<Git.VcInternalException>(() => service.CommitNativeBaseline(
            shared.RepositoryUri,
            source,
            "native: initial managed TIA project baseline"));

        Assert.Equal("SVN_CHECKOUT_FAILED", error.Code);
        Assert.Equal(2, checkoutCalls);
        Assert.True(Directory.Exists(source));
        Assert.Equal("project", File.ReadAllText(Path.Combine(source, "Line.ap17")));
        Assert.Empty(Directory.GetDirectories(_root, ".svn-native-*"));

        var log = _svn.Log(MainUrl(shared), allHistory: true);
        Assert.Single(log.Entries);
    }

    [Fact]
    public void Commit_TextAndBinaryFiles_ReturnsIncrementingRevisions()
    {
        var shared = CreateSharedWithMain();
        var workingCopy = Path.Combine(_root, "tia");
        _svn.Checkout(MainUrl(shared), workingCopy);

        File.WriteAllText(Path.Combine(workingCopy, "notes.txt"), "hello svn");
        File.WriteAllBytes(Path.Combine(workingCopy, "project.bin"), new byte[] { 0x00, 0x01, 0xFE, 0xFF });
        _svn.AddRecursive(workingCopy);
        var first = _svn.Commit(workingCopy, "add text and binary");

        File.WriteAllText(Path.Combine(workingCopy, "notes.txt"), "hello svn v2");
        var second = _svn.Commit(workingCopy, "modify text");

        Assert.True(first.Committed);
        Assert.True(second.Committed);
        Assert.True(second.Revision > first.Revision);

        // Binary round-trips intact through the repository.
        var roundTrip = Path.Combine(_root, "tia-roundtrip");
        _svn.Checkout(MainUrl(shared), roundTrip);
        Assert.Equal(
            new byte[] { 0x00, 0x01, 0xFE, 0xFF },
            File.ReadAllBytes(Path.Combine(roundTrip, "project.bin")));
    }

    [Fact]
    public void Commit_ReadOnlyWorkingCopyMetadata_StillReturnsCommittedRevision()
    {
        var shared = CreateSharedWithMain();
        var workingCopy = Path.Combine(_root, "tia-read-only");
        _svn.Checkout(MainUrl(shared), workingCopy);

        var projectFile = Path.Combine(workingCopy, "project.txt");
        File.WriteAllText(projectFile, "v1");
        _svn.AddRecursive(workingCopy);
        SetReadOnlyRecursively(workingCopy);

        var commit = _svn.Commit(workingCopy, "commit read-only working copy");

        Assert.True(commit.Committed);
        Assert.True(commit.Revision > 0);
    }

    private static void SetReadOnlyRecursively(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Append(root))
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
    }

    [Fact]
    public void CopyBranch_CommitOnBranch_DoesNotAdvanceMain()
    {
        var shared = CreateSharedWithMain();
        var main = Path.Combine(_root, "tia-main");
        _svn.Checkout(MainUrl(shared), main);
        File.WriteAllText(Path.Combine(main, "base.txt"), "base");
        _svn.AddRecursive(main);
        var baseCommit = _svn.Commit(main, "baseline");

        // native/branches does not exist yet: the copy creates it on demand.
        var copy = _svn.CopyBranch(MainUrl(shared), baseCommit.Revision, "feature-x", "branch feature-x");
        Assert.EndsWith("/native/branches/feature-x", copy.BranchUrl);
        Assert.True(copy.Revision > baseCommit.Revision);

        var feature = Path.Combine(_root, "tia-feature");
        _svn.Checkout(copy.BranchUrl, feature);
        File.WriteAllText(Path.Combine(feature, "feature.txt"), "feature work");
        _svn.AddRecursive(feature);
        var featureCommit = _svn.Commit(feature, "feature change");

        // Main still has the branch copy as its latest revision; the feature commit
        // only appears in the branch log.
        var mainLog = _svn.Log(MainUrl(shared), 10);
        Assert.DoesNotContain(mainLog.Entries, e => e.Revision == featureCommit.Revision);
        var branchLog = _svn.Log(copy.BranchUrl, 10);
        Assert.Contains(branchLog.Entries, e => e.Revision == featureCommit.Revision);

        // The branch copy was taken at the peg revision: main content is present.
        Assert.True(File.Exists(Path.Combine(feature, "base.txt")));
    }

    [Fact]
    public void Log_AllHistoryReturnsEveryEntry()
    {
        var shared = CreateSharedWithMain();
        var workingCopy = Path.Combine(_root, "tia-all-history");
        _svn.Checkout(MainUrl(shared), workingCopy);

        File.WriteAllText(Path.Combine(workingCopy, "one.txt"), "one");
        _svn.AddRecursive(workingCopy);
        _svn.Commit(workingCopy, "one");
        File.WriteAllText(Path.Combine(workingCopy, "one.txt"), "two");
        _svn.Commit(workingCopy, "two");
        File.WriteAllText(Path.Combine(workingCopy, "one.txt"), "three");
        _svn.Commit(workingCopy, "three");

        var log = _svn.Log(MainUrl(shared), allHistory: true);

        Assert.Contains(log.Entries, entry => entry.Message == "one");
        Assert.Contains(log.Entries, entry => entry.Message == "two");
        Assert.Contains(log.Entries, entry => entry.Message == "three");
    }

    [Fact]
    public void Log_ReadsHistoryFromAWorkingCopyPath()
    {
        var shared = CreateSharedWithMain();
        var workingCopy = Path.Combine(_root, "tia-working-copy-log");
        _svn.Checkout(MainUrl(shared), workingCopy);

        File.WriteAllText(Path.Combine(workingCopy, "history.txt"), "one");
        _svn.AddRecursive(workingCopy);
        _svn.Commit(workingCopy, "working copy history");

        var info = _svn.Info(workingCopy);
        Assert.Equal(MainUrl(shared), info.Uri);

        var log = _svn.Log(workingCopy, 10);

        Assert.Contains(log.Entries, entry => entry.Message == "working copy history");
    }

    [Fact]
    public void UpdateToRevision_MovesWorkingCopyBackAndForward()
    {
        var shared = CreateSharedWithMain();
        var workingCopy = Path.Combine(_root, "tia");
        _svn.Checkout(MainUrl(shared), workingCopy);

        File.WriteAllText(Path.Combine(workingCopy, "data.txt"), "v1");
        _svn.AddRecursive(workingCopy);
        var first = _svn.Commit(workingCopy, "v1");
        File.WriteAllText(Path.Combine(workingCopy, "data.txt"), "v2");
        var second = _svn.Commit(workingCopy, "v2");
        Assert.Equal("v2", File.ReadAllText(Path.Combine(workingCopy, "data.txt")));

        var back = _svn.UpdateToRevision(workingCopy, first.Revision);
        Assert.Equal(first.Revision, back.Revision);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(workingCopy, "data.txt")));
        Assert.Equal(first.Revision, _svn.Info(workingCopy).Revision);

        var forward = _svn.UpdateToRevision(workingCopy, second.Revision);
        Assert.Equal(second.Revision, forward.Revision);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(workingCopy, "data.txt")));
    }

    [Fact]
    public void Status_ReportsCleanAndDirty()
    {
        var shared = CreateSharedWithMain();
        var workingCopy = Path.Combine(_root, "tia");
        _svn.Checkout(MainUrl(shared), workingCopy);

        Assert.True(_svn.Status(workingCopy).IsClean);

        File.WriteAllText(Path.Combine(workingCopy, "dirty.txt"), "unversioned");
        var dirty = _svn.Status(workingCopy);
        Assert.False(dirty.IsClean);
        Assert.Contains(dirty.Entries, e => e.Path == "dirty.txt" && e.NodeStatus == "NotVersioned");
    }

    [Fact]
    public void Info_ReturnsUrlAndRevision()
    {
        var shared = CreateSharedWithMain();
        var workingCopy = Path.Combine(_root, "tia");
        _svn.Checkout(MainUrl(shared), workingCopy);

        var info = _svn.Info(workingCopy);

        Assert.EndsWith("/native/main", info.Uri);
        Assert.True(info.Revision >= 1);
    }

    [Fact]
    public void Errors_MapToVcShape()
    {
        var error = Assert.Throws<Git.VcInternalException>(
            () => _svn.Status(Path.Combine(_root, "not-a-working-copy")));
        Assert.Equal("SVN_STATUS_FAILED", error.Code);
        Assert.False(string.IsNullOrEmpty(error.Message));
    }
}
