using System;
using Mcp.VersionControl.Svn;
using Xunit;

namespace Mcp.VersionControl.Tests.Svn;

public sealed class SvnCommitNoChangeTests : IDisposable
{
    private readonly string _root;
    private readonly SvnRepositoryService _svn = new();

    public SvnCommitNoChangeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "McpVcSvnNoChange", Guid.NewGuid().ToString("N"));
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
    public void Commit_WithoutChanges_ReturnsRepositoryHeadWithCommittedFalse()
    {
        var shared = _svn.CreateShared(Path.Combine(_root, "workbench"));
        var mainUrl = shared.RepositoryUri.TrimEnd('/') + "/native/main";
        var workingCopy = Path.Combine(_root, "tia");
        _svn.Checkout(mainUrl, workingCopy);
        File.WriteAllText(Path.Combine(workingCopy, "Line.ap17"), "v1");
        _svn.AddRecursive(workingCopy);
        var baseline = _svn.Commit(workingCopy, "baseline");
        Assert.True(baseline.Committed);

        var noChange = _svn.Commit(workingCopy, "nothing to commit");

        Assert.False(noChange.Committed);
        Assert.Equal(baseline.Revision, noChange.Revision);

        // A later real change still commits and advances the repository.
        File.AppendAllText(Path.Combine(workingCopy, "Line.ap17"), "v2");
        _svn.AddRecursive(workingCopy);
        var second = _svn.Commit(workingCopy, "second");
        Assert.True(second.Committed);
        Assert.Equal(baseline.Revision + 1, second.Revision);
    }
}
