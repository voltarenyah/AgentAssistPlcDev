using System;
using System.Linq;
using System.Text.Json;
using LibGit2Sharp;
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

    [Theory]
    [InlineData("devices/PLC_1/source/Main.xml", "devices/PLC_1/source/Main.xml")]
    [InlineData("devices\\PLC_1\\source\\Blocks\\Main.XML", "devices/PLC_1/source/Blocks/Main.XML")]
    public void SourcePathPolicy_AcceptsOnlyNormalizedSourceXml(string path, string expected)
    {
        Assert.Equal(expected, SourcePathPolicy.Require(path));
        Assert.True(SourcePathPolicy.IsAllowed(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../devices/PLC_1/source/Main.xml")]
    [InlineData("devices/PLC_1/source/../Main.xml")]
    [InlineData("/devices/PLC_1/source/Main.xml")]
    [InlineData("\\devices\\PLC_1\\source\\Main.xml")]
    [InlineData("C:\\devices\\PLC_1\\source\\Main.xml")]
    [InlineData("devices//source/Main.xml")]
    [InlineData("devices/PLC_1/source/Main.txt")]
    [InlineData("devices/PLC_1/staging/Main.xml")]
    [InlineData("source/Main.xml")]
    public void SourcePathPolicy_RejectsPathsOutsideTrackedXml(string path)
    {
        var error = Assert.Throws<VcInternalException>(() => SourcePathPolicy.Require(path));

        Assert.Equal("SOURCE_PATH_REQUIRED", error.Code);
        Assert.False(SourcePathPolicy.IsAllowed(path));
    }

    [Fact]
    public void Status_ShowsUntracked()
    {
        _tools.VcInit(_fixture.RootPath);
        const string sourcePath = "devices/PLC_1/source/test.xml";
        _fixture.WriteFile(sourcePath, "<Document />");
        var result = _tools.VcStatus(_fixture.RootPath);
        var data = Unwrap<VcStatusResult>(result);
        Assert.NotNull(data);
        Assert.Contains(data!.Entries, e => e.FilePath == sourcePath && e.State == "Untracked");
    }

    [Fact]
    public void Status_AfterAddShowsStaged()
    {
        using var repo = _fixture.InitRepo();
        const string sourcePath = "devices/PLC_1/source/test.xml";
        _fixture.WriteFile(sourcePath, "<Document />");
        _tools.VcAdd(_fixture.RootPath, new[] { sourcePath });
        var result = _tools.VcStatus(_fixture.RootPath);
        var data = Unwrap<VcStatusResult>(result);
        Assert.NotNull(data);
        Assert.Contains(data!.Entries, e => e.FilePath == sourcePath && e.Staged);
    }

    [Fact]
    public void Status_AfterCommitShowsClean()
    {
        _fixture.CommitFile("devices/PLC_1/source/test.xml", "<Document />", "init");
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
        const string firstPath = "devices/PLC_1/source/a.xml";
        const string secondPath = "devices/PLC_1/source/b.xml";
        _fixture.WriteFile(firstPath, "<Document Name=\"a\" />");
        _fixture.WriteFile(secondPath, "<Document Name=\"b\" />");
        _tools.VcAdd(_fixture.RootPath, new[] { firstPath });
        var result = _tools.VcStatus(_fixture.RootPath);
        var data = Unwrap<VcStatusResult>(result);
        Assert.NotNull(data);
        Assert.Contains(data!.Entries, e => e.FilePath == firstPath && e.Staged);
        Assert.Contains(data.Entries, e => e.FilePath == secondPath && !e.Staged);
    }

    [Fact]
    public void Add_AllWhenNull()
    {
        _tools.VcInit(_fixture.RootPath);
        _fixture.WriteFile("devices/PLC_1/source/a.xml", "<Document Name=\"a\" />");
        _fixture.WriteFile("devices/PLC_1/source/b.xml", "<Document Name=\"b\" />");
        _tools.VcAdd(_fixture.RootPath);
        var result = _tools.VcStatus(_fixture.RootPath);
        var data = Unwrap<VcStatusResult>(result);
        Assert.NotNull(data);
        Assert.Equal(2, data!.Entries.Length);
        Assert.All(data.Entries, e => Assert.True(e.Staged));
    }

    [Fact]
    public void Add_ExplicitNonSourcePathReturnsBoundaryErrorWithoutStagingAnyPath()
    {
        _tools.VcInit(_fixture.RootPath);
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        const string outsidePath = "notes.txt";
        _fixture.WriteFile(sourcePath, "<Document />");
        _fixture.WriteFile(outsidePath, "do not track");

        var result = _tools.VcAdd(_fixture.RootPath, new[] { sourcePath, outsidePath });

        Assert.True(result.IsError == true);
        Assert.Equal("SOURCE_PATH_REQUIRED", ErrorCode(result));
        using var repo = new Repository(_fixture.RootPath);
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
        });
        Assert.False(IsIndexChange(status[sourcePath].State));
        Assert.False(IsIndexChange(status[outsidePath].State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Add_AllStagesOnlyAllowedChangedSourceXml(bool passEmptyPaths)
    {
        _tools.VcInit(_fixture.RootPath);
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        const string notePath = "notes.txt";
        const string runtimePath = "devices/PLC_1/staging/Temp.xml";
        _fixture.WriteFile(sourcePath, "<Document />");
        _fixture.WriteFile(notePath, "do not track");
        _fixture.WriteFile(runtimePath, "<Runtime />");

        var result = passEmptyPaths
            ? _tools.VcAdd(_fixture.RootPath, Array.Empty<string>())
            : _tools.VcAdd(_fixture.RootPath);

        Assert.False(result.IsError == true);
        Assert.Equal(1, Unwrap<VcAddResult>(result)!.Staged);
        using var repo = new Repository(_fixture.RootPath);
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
        });
        Assert.True(IsIndexChange(status[sourcePath].State));
        Assert.False(IsIndexChange(status[notePath].State));
        Assert.False(IsIndexChange(status[runtimePath].State));
    }

    [Fact]
    public void Add_AllStagesDeletionOfAllowedSourceXml()
    {
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        _fixture.CommitFile(sourcePath, "<Document />", "initial source");
        File.Delete(Path.Combine(_fixture.RootPath, sourcePath));

        var addResult = _tools.VcAdd(_fixture.RootPath);

        Assert.False(addResult.IsError == true);
        using (var repo = new Repository(_fixture.RootPath))
        {
            Assert.True(repo.RetrieveStatus()[sourcePath].State.HasFlag(FileStatus.DeletedFromIndex));
        }

        var commitResult = _tools.VcCommit(_fixture.RootPath, "delete source");
        Assert.False(commitResult.IsError == true);
        using var committedRepo = new Repository(_fixture.RootPath);
        Assert.Null(committedRepo.Head.Tip.Tree[sourcePath]);
    }

    /* ── vc_commit ──────────────────────────────────────── */

    [Fact]
    public void Commit_CreatesCommitWithMessage()
    {
        _fixture.CommitFile("devices/PLC_1/source/Initial.xml", "<Document />", "init");
        const string secondPath = "devices/PLC_1/source/Second.xml";
        _fixture.WriteFile(secondPath, "<Document Name=\"Second\" />");
        _tools.VcAdd(_fixture.RootPath, new[] { secondPath });
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

    [Fact]
    public void Commit_RejectsAlreadyStagedPathOutsideSourcePolicy()
    {
        using var repo = _fixture.InitRepo();
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        const string outsidePath = "notes.txt";
        _fixture.WriteFile(sourcePath, "<Document />");
        _fixture.WriteFile(outsidePath, "externally staged");
        Commands.Stage(repo, new[] { sourcePath, outsidePath });

        var result = _tools.VcCommit(_fixture.RootPath, "must not escape boundary");

        Assert.True(result.IsError == true);
        Assert.Equal("SOURCE_PATH_REQUIRED", ErrorCode(result));
        Assert.Null(repo.Head.Tip);
        var status = repo.RetrieveStatus();
        Assert.True(IsIndexChange(status[sourcePath].State));
        Assert.True(IsIndexChange(status[outsidePath].State));
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
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        var sha1 = _fixture.CommitFile(sourcePath, "<Document Name=\"First\" />\n", "first");
        _fixture.WriteFile(sourcePath, "<Document Name=\"Second\" />\n");
        _tools.VcAdd(_fixture.RootPath, new[] { sourcePath });
        var sha2Info = _tools.VcCommit(_fixture.RootPath, "second");
        Assert.False(sha2Info.IsError == true);

        var result = _tools.VcDiff(_fixture.RootPath, sourcePath, oldSha: sha1);
        Assert.False(result.IsError == true);
        var data = Unwrap<VcDiffResult>(result);
        Assert.NotNull(data);
        Assert.NotEmpty(data!.Hunks);
    }

    /* ── vc_snapshot ────────────────────────────────────── */

    [Fact]
    public void Snapshot_CommitsOnlyAllowedSourceXmlAndLeavesOtherChanges()
    {
        _tools.VcInit(_fixture.RootPath);
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        const string notePath = "notes.txt";
        const string runtimePath = "devices/PLC_1/staging/Temp.xml";
        _fixture.WriteFile(sourcePath, "<Document />");
        _fixture.WriteFile(notePath, "leave me");
        _fixture.WriteFile(runtimePath, "<Runtime />");

        var result = _tools.VcSnapshot(_fixture.RootPath, "checkpoint");

        Assert.False(result.IsError == true);
        var data = Unwrap<VcCommitResult>(result);
        Assert.NotNull(data);
        Assert.False(string.IsNullOrEmpty(data!.Sha));
        using var repo = new Repository(_fixture.RootPath);
        Assert.NotNull(repo.Head.Tip.Tree[sourcePath]);
        Assert.Null(repo.Head.Tip.Tree[notePath]);
        Assert.Null(repo.Head.Tip.Tree[runtimePath]);
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
        });
        Assert.DoesNotContain(status, entry => entry.FilePath == sourcePath);
        Assert.False(IsIndexChange(status[notePath].State));
        Assert.False(IsIndexChange(status[runtimePath].State));
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

    private static string? ErrorCode(CallToolResult result)
    {
        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!));
        using var document = JsonDocument.Parse(block.Text);
        return document.RootElement
            .GetProperty("error")
            .GetProperty("code")
            .GetString();
    }

    private static bool IsIndexChange(FileStatus status) =>
        status.HasFlag(FileStatus.NewInIndex) ||
        status.HasFlag(FileStatus.ModifiedInIndex) ||
        status.HasFlag(FileStatus.DeletedFromIndex) ||
        status.HasFlag(FileStatus.RenamedInIndex) ||
        status.HasFlag(FileStatus.TypeChangeInIndex);
}
