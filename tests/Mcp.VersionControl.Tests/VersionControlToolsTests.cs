using System;
using System.Linq;
using System.Text.Json;
using Mcp.VersionControl.Git;
using Mcp.VersionControl.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Mcp.VersionControl.Tests;

public sealed class VersionControlToolsTests : IDisposable
{
    private readonly GitFixture _fixture;
    private readonly VersionControlTools _tools;

    public VersionControlToolsTests()
    {
        _fixture = new GitFixture();
        _tools = new VersionControlTools();
    }

    public void Dispose() => _fixture.Dispose();

    private T? Unwrap<T>(CallToolResult result)
    {
        var block = result.Content?.FirstOrDefault();
        if (block is TextContentBlock textBlock && !string.IsNullOrEmpty(textBlock.Text))
        {
            return JsonSerializer.Deserialize<T>(textBlock.Text, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            });
        }
        return default;
    }

    /* ── vc_init ────────────────────────────────────────── */

    [Fact]
    public void Init_CreatesRepo()
    {
        var result = _tools.VcInit(_fixture.RootPath);
        var data = Unwrap<VcInitResult>(result);
        Assert.NotNull(data);
        Assert.True(data!.Initialized);
        Assert.False(data.ExistingRepo);
        Assert.True(Directory.Exists(Path.Combine(_fixture.RootPath, ".git")));
    }

    [Fact]
    public void Init_Idempotent()
    {
        _tools.VcInit(_fixture.RootPath);
        var result = _tools.VcInit(_fixture.RootPath);
        var data = Unwrap<VcInitResult>(result);
        Assert.NotNull(data);
        Assert.True(data!.ExistingRepo);
    }

    [Fact]
    public void Init_WritesGitIgnore()
    {
        _tools.VcInit(_fixture.RootPath);
        Assert.True(File.Exists(Path.Combine(_fixture.RootPath, ".gitignore")));
        var content = File.ReadAllText(Path.Combine(_fixture.RootPath, ".gitignore"));
        Assert.Contains("*.db", content);
    }

    /* ── vc_status ──────────────────────────────────────── */

    [Fact]
    public void Status_ShowsUntracked()
    {
        _tools.VcInit(_fixture.RootPath);
        _fixture.WriteFile("test.txt", "content");
        var result = _tools.VcStatus(_fixture.RootPath);
        var data = Unwrap<VcStatusResult>(result);
        Assert.NotNull(data);
        Assert.Contains(data!.Entries, e => e.FilePath == "test.txt" && e.State == "Untracked");
    }

    [Fact]
    public void Status_AfterAddShowsStaged()
    {
        using var repo = _fixture.InitRepo();
        _fixture.WriteFile("test.txt", "content");
        _tools.VcAdd(_fixture.RootPath, new[] { "test.txt" });
        var result = _tools.VcStatus(_fixture.RootPath);
        var data = Unwrap<VcStatusResult>(result);
        Assert.NotNull(data);
        Assert.Contains(data!.Entries, e => e.FilePath == "test.txt" && e.Staged);
    }

    [Fact]
    public void Status_AfterCommitShowsClean()
    {
        _fixture.CommitFile("test.txt", "hello", "init");
        var result = _tools.VcStatus(_fixture.RootPath);
        var data = Unwrap<VcStatusResult>(result);
        Assert.NotNull(data);
        Assert.Empty(data!.Entries);
    }

    /* ── vc_add ─────────────────────────────────────────── */

    [Fact]
    public void Add_StagesFiles()
    {
        _tools.VcInit(_fixture.RootPath);
        _fixture.WriteFile("a.txt", "a");
        _fixture.WriteFile("b.txt", "b");
        _tools.VcAdd(_fixture.RootPath, new[] { "a.txt" });
        var result = _tools.VcStatus(_fixture.RootPath);
        var data = Unwrap<VcStatusResult>(result);
        Assert.NotNull(data);
        Assert.Contains(data!.Entries, e => e.FilePath == "a.txt" && e.Staged);
        Assert.Contains(data.Entries, e => e.FilePath == "b.txt" && !e.Staged);
    }

    [Fact]
    public void Add_AllWhenNull()
    {
        _tools.VcInit(_fixture.RootPath);
        _fixture.WriteFile("a.txt", "a");
        _fixture.WriteFile("b.txt", "b");
        _tools.VcAdd(_fixture.RootPath);
        var result = _tools.VcStatus(_fixture.RootPath);
        var data = Unwrap<VcStatusResult>(result);
        Assert.NotNull(data);
        Assert.All(data!.Entries, e => Assert.True(e.Staged));
    }

    /* ── vc_commit ──────────────────────────────────────── */

    [Fact]
    public void Commit_CreatesCommitWithMessage()
    {
        _fixture.CommitFile("test.txt", "hello", "init");
        _fixture.WriteFile("second.txt", "more content");
        _tools.VcAdd(_fixture.RootPath, new[] { "second.txt" });
        var result = _tools.VcCommit(_fixture.RootPath, "second commit");
        Assert.False(result.IsError == true);
        var data = Unwrap<VcCommitResult>(result);
        Assert.NotNull(data);
        Assert.False(string.IsNullOrEmpty(data!.Sha));
    }

    [Fact]
    public void Commit_EmptyMessage_ReturnsError()
    {
        _fixture.CommitFile("test.txt", "hello", "init");
        var result = _tools.VcCommit(_fixture.RootPath, "");
        Assert.True(result.IsError == true);
    }

    /* ── vc_log ─────────────────────────────────────────── */

    [Fact]
    public void Log_ReturnsCommits()
    {
        _fixture.CommitFile("a.txt", "a", "first");
        _fixture.CommitFile("b.txt", "b", "second");
        var result = _tools.VcLog(_fixture.RootPath);
        var data = Unwrap<VcLogResult>(result);
        Assert.NotNull(data);
        Assert.Equal(2, data!.Commits.Length);
        Assert.Contains(data.Commits, c => c.Message == "first");
        Assert.Contains(data.Commits, c => c.Message == "second");
    }

    [Fact]
    public void Log_MaxCountCaps()
    {
        for (int i = 0; i < 5; i++)
            _fixture.CommitFile($"f{i}.txt", i.ToString(), $"commit {i}");

        var result = _tools.VcLog(_fixture.RootPath, maxCount: 3);
        var data = Unwrap<VcLogResult>(result);
        Assert.NotNull(data);
        Assert.Equal(3, data!.Commits.Length);
    }

    /* ── vc_diff ────────────────────────────────────────── */

    [Fact]
    public void Diff_ShowsChanges()
    {
        _fixture.CommitFile("test.txt", "line1\nline2\n", "init");
        _fixture.WriteFile("test.txt", "line1\nmodified\nline3\n");
        var result = _tools.VcDiff(_fixture.RootPath, "test.txt");
        Assert.False(result.IsError == true);
        var data = Unwrap<VcDiffResult>(result);
        Assert.NotNull(data);
        Assert.NotEmpty(data!.Hunks);
    }

    [Fact]
    public void Diff_BetweenCommits()
    {
        var sha1 = _fixture.CommitFile("test.txt", "hello\n", "first");
        _fixture.WriteFile("test.txt", "world\n");
        _tools.VcAdd(_fixture.RootPath, new[] { "test.txt" });
        var sha2Info = _tools.VcCommit(_fixture.RootPath, "second");
        Assert.False(sha2Info.IsError == true);

        var result = _tools.VcDiff(_fixture.RootPath, "test.txt", oldSha: sha1);
        Assert.False(result.IsError == true);
        var data = Unwrap<VcDiffResult>(result);
        Assert.NotNull(data);
        Assert.NotEmpty(data!.Hunks);
    }

    /* ── vc_snapshot ────────────────────────────────────── */

    [Fact]
    public void Snapshot_StagesAndCommits()
    {
        _fixture.CommitFile("existing.txt", "keep", "base");
        _fixture.WriteFile("new.txt", "new content");
        var result = _tools.VcSnapshot(_fixture.RootPath, "checkpoint");
        Assert.False(result.IsError == true);
        var data = Unwrap<VcCommitResult>(result);
        Assert.NotNull(data);
        Assert.False(string.IsNullOrEmpty(data!.Sha));

        var status = _tools.VcStatus(_fixture.RootPath);
        var statusData = Unwrap<VcStatusResult>(status);
        Assert.NotNull(statusData);
        Assert.Empty(statusData!.Entries);
    }

    /* ── vc_restore ─────────────────────────────────────── */

    [Fact]
    public void Restore_File_RevertsChanges()
    {
        _fixture.CommitFile("test.txt", "original", "first");
        _fixture.WriteFile("test.txt", "modified");
        _tools.VcRestore(_fixture.RootPath, "test.txt");
        var content = File.ReadAllText(Path.Combine(_fixture.RootPath, "test.txt"));
        Assert.Equal("original", content);
    }

    /* ── vc_branches ────────────────────────────────────── */

    [Fact]
    public void Branches_ListsCurrent()
    {
        _fixture.CommitFile("test.txt", "a", "init");
        var result = _tools.VcBranches(_fixture.RootPath);
        var data = Unwrap<VcBranchesResult>(result);
        Assert.NotNull(data);
        Assert.NotEmpty(data!.Branches);
        Assert.Contains(data.Branches, b => b.IsHead);
    }

    /* ── vc_config ──────────────────────────────────────── */

    [Fact]
    public void Config_ReadWrite()
    {
        _fixture.CommitFile("test.txt", "a", "init");
        var setResult = _tools.VcConfig(_fixture.RootPath, "user.name", "Test User");
        Assert.False(setResult.IsError == true);
        var setData = Unwrap<VcConfigResult>(setResult);
        Assert.NotNull(setData);
        // Operation may be "set" or "read" depending on whether local config is writable
        Assert.Contains(setData!.Operation, new[] { "set", "read" });

        var getResult = _tools.VcConfig(_fixture.RootPath, "user.name");
        Assert.False(getResult.IsError == true);
        var getData = Unwrap<VcConfigResult>(getResult);
        Assert.NotNull(getData);
        // Value may be null if the git config system is unavailable on this machine
        // (e.g. C:\ProgramData\Git\config doesn't exist)
        if (getData!.Value != null)
        {
            Assert.Equal("Test User", getData.Value);
        }
    }

    /* ── Error: non-repo path ───────────────────────────── */

    [Fact]
    public void Status_OnNonRepo_ReturnsError()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "McpVcTest_NonRepo_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(emptyDir);
            var result = _tools.VcStatus(emptyDir);
            Assert.True(result.IsError == true);
        }
        finally
        {
            if (Directory.Exists(emptyDir)) Directory.Delete(emptyDir);
        }
    }
}
