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

    private SvnSharedInitResult CreateShared()
    {
        var result = _svn.CreateShared(Path.Combine(_root, "workbench"));
        Assert.True(Directory.Exists(result.RepositoryPath));
        return result;
    }

    private string MainUrl(SvnSharedInitResult shared) => shared.RepositoryUri.TrimEnd('/') + "/native/main";

    [Fact]
    public void CreateShared_CreatesRepositoryAndLayoutDirs()
    {
        var shared = CreateShared();

        Assert.True(shared.Initialized);
        Assert.StartsWith("file:///", shared.RepositoryUri);

        // The layout dirs must exist in the repository: check out ^/native and inspect.
        var nativeCheckout = Path.Combine(_root, "native-wc");
        _svn.Checkout(shared.RepositoryUri.TrimEnd('/') + "/native", nativeCheckout);
        Assert.True(Directory.Exists(Path.Combine(nativeCheckout, "main")));
        Assert.True(Directory.Exists(Path.Combine(nativeCheckout, "branches")));
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
    public void Checkout_EmptyMain_CreatesCleanWorkingCopy()
    {
        var shared = CreateShared();
        var workingCopy = Path.Combine(_root, "tia");

        var result = _svn.Checkout(MainUrl(shared), workingCopy);

        Assert.True(Directory.Exists(workingCopy));
        Assert.True(result.Revision >= 0);
        var status = _svn.Status(workingCopy);
        Assert.True(status.IsClean);
        Assert.Empty(status.Entries);
    }

    [Fact]
    public void Commit_TextAndBinaryFiles_ReturnsIncrementingRevisions()
    {
        var shared = CreateShared();
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
    public void CopyBranch_CommitOnBranch_DoesNotAdvanceMain()
    {
        var shared = CreateShared();
        var main = Path.Combine(_root, "tia-main");
        _svn.Checkout(MainUrl(shared), main);
        File.WriteAllText(Path.Combine(main, "base.txt"), "base");
        _svn.AddRecursive(main);
        var baseCommit = _svn.Commit(main, "baseline");

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
    public void UpdateToRevision_MovesWorkingCopyBackAndForward()
    {
        var shared = CreateShared();
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
        var shared = CreateShared();
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
        var shared = CreateShared();
        var workingCopy = Path.Combine(_root, "tia");
        _svn.Checkout(MainUrl(shared), workingCopy);

        var info = _svn.Info(workingCopy);

        Assert.EndsWith("/native/main", info.Uri);
        Assert.True(info.Revision >= 0);
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
