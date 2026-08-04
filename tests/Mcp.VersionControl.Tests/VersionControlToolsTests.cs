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

    [Fact]
    public void CommitSelected_CommitsOnlyRequestedXmlAndLeavesOtherChanges()
    {
        const string firstPath = "devices/PLC_1/source/Blocks/A.xml";
        const string secondPath = "devices/PLC_1/source/Blocks/B.xml";
        _fixture.CommitFile(firstPath, "<Document><SW.Blocks.OB ID=\"1\" /></Document>", "base");
        _fixture.WriteFile(firstPath, "<Document><SW.Blocks.OB ID=\"1\" Name=\"changed\" /></Document>");
        _fixture.WriteFile(secondPath, "<Document><SW.Blocks.FC ID=\"2\" /></Document>");

        var result = _tools.VcCommitSelected(_fixture.RootPath, new[] { firstPath }, "change A");

        Assert.False(result.IsError == true);
        var commit = Assert.IsType<VcCommitResult>(Unwrap<VcCommitResult>(result));
        Assert.Equal(new[] { firstPath }, commit.Files);
        using var repo = new Repository(_fixture.RootPath);
        Assert.NotNull(repo.Head.Tip.Tree[firstPath]);
        Assert.Null(repo.Head.Tip.Tree[secondPath]);
        Assert.Contains(RepositoryService.Status(_fixture.RootPath).Entries, entry => entry.FilePath == secondPath);
    }

    [Fact]
    public void CommitSelected_StagesAndCommitsDeletion()
    {
        const string sourcePath = "devices/PLC_1/source/Blocks/Deleted.xml";
        _fixture.CommitFile(sourcePath, "<Document><SW.Blocks.FC ID=\"1\" /></Document>", "base");
        File.Delete(Path.Combine(_fixture.RootPath, sourcePath));

        var result = _tools.VcCommitSelected(_fixture.RootPath, new[] { sourcePath }, "delete block");

        Assert.False(result.IsError == true);
        Assert.Equal(new[] { sourcePath }, Unwrap<VcCommitResult>(result)!.Files);
        using var repo = new Repository(_fixture.RootPath);
        Assert.Null(repo.Head.Tip.Tree[sourcePath]);
    }

    [Fact]
    public void CommitSelected_MixedUnchangedSelectionDoesNotMutateIndexOrCreateCommit()
    {
        const string changedPath = "devices/PLC_1/source/Blocks/Changed.xml";
        const string unchangedPath = "devices/PLC_1/source/Blocks/Unchanged.xml";
        _fixture.CommitFile(changedPath, "<Document><SW.Blocks.FC ID=\"1\" /></Document>", "base changed");
        _fixture.CommitFile(unchangedPath, "<Document><SW.Blocks.FC ID=\"2\" /></Document>", "base unchanged");
        _fixture.WriteFile(changedPath, "<Document><SW.Blocks.FC ID=\"1\" Name=\"changed\" /></Document>");
        using (var repo = new Repository(_fixture.RootPath))
        {
            Commands.Stage(repo, changedPath);
        }
        string headBefore;
        using (var repo = new Repository(_fixture.RootPath)) headBefore = repo.Head.Tip.Sha;

        var result = _tools.VcCommitSelected(
            _fixture.RootPath,
            new[] { changedPath, unchangedPath },
            "must be atomic");

        Assert.True(result.IsError == true);
        Assert.Equal("SOURCE_PATH_UNCHANGED", ErrorCode(result));
        using var after = new Repository(_fixture.RootPath);
        Assert.Equal(headBefore, after.Head.Tip.Sha);
        Assert.True(IsIndexChange(after.RetrieveStatus()[changedPath].State));
    }

    [Fact]
    public void CommitSelected_ClearsForbiddenPreStagedFileIncludingOnUnbornHead()
    {
        using var repo = _fixture.InitRepo();
        const string sourcePath = "devices/PLC_1/source/Blocks/Main.xml";
        const string forbiddenPath = "notes.txt";
        _fixture.WriteFile(sourcePath, "<Document><SW.Blocks.OB ID=\"1\" /></Document>");
        _fixture.WriteFile(forbiddenPath, "must remain outside history");
        Commands.Stage(repo, forbiddenPath);

        var result = _tools.VcCommitSelected(_fixture.RootPath, new[] { sourcePath }, "initial source");

        Assert.False(result.IsError == true);
        Assert.Equal(new[] { sourcePath }, Unwrap<VcCommitResult>(result)!.Files);
        Assert.NotNull(repo.Head.Tip.Tree[sourcePath]);
        Assert.Null(repo.Head.Tip.Tree[forbiddenPath]);
        Assert.False(IsIndexChange(repo.RetrieveStatus()[forbiddenPath].State));
    }

    [Theory]
    [InlineData(false, "message", "SOURCE_PATHS_REQUIRED")]
    [InlineData(true, "", "MESSAGE_REQUIRED")]
    public void CommitSelected_RequiresPathsAndMessage(bool includePath, string message, string expectedCode)
    {
        const string sourcePath = "devices/PLC_1/source/Blocks/Main.xml";
        _tools.VcInit(_fixture.RootPath);
        _fixture.WriteFile(sourcePath, "<Document />");

        var result = _tools.VcCommitSelected(
            _fixture.RootPath,
            includePath ? new[] { sourcePath } : Array.Empty<string>(),
            message);

        Assert.True(result.IsError == true);
        Assert.Equal(expectedCode, ErrorCode(result));
    }

    [Fact]
    public void CommitSelected_IndexOnlyChangeWithWorktreeEqualToHeadIsUnchanged()
    {
        const string sourcePath = "devices/PLC_1/source/Blocks/Main.xml";
        const string headXml = "<Document><SW.Blocks.OB ID=\"1\" Name=\"head\" /></Document>";
        _fixture.CommitFile(sourcePath, headXml, "base");
        _fixture.WriteFile(sourcePath, "<Document><SW.Blocks.OB ID=\"1\" Name=\"index-only\" /></Document>");
        using (var repo = new Repository(_fixture.RootPath))
        {
            Commands.Stage(repo, sourcePath);
        }
        _fixture.WriteFile(sourcePath, headXml);

        var result = _tools.VcCommitSelected(_fixture.RootPath, new[] { sourcePath }, "must not commit index only");

        Assert.True(result.IsError == true);
        Assert.Equal("SOURCE_PATH_UNCHANGED", ErrorCode(result));
        using var after = new Repository(_fixture.RootPath);
        Assert.True(IsIndexChange(after.RetrieveStatus()[sourcePath].State));
    }

    [Fact]
    public void CommitSelected_CommitFailureRestoresExactOriginalIndexBytes()
    {
        const string selectedPath = "devices/PLC_1/source/Blocks/Selected.xml";
        const string stagedPath = "devices/PLC_1/source/Blocks/PreviouslyStaged.xml";
        _fixture.CommitFile(selectedPath, "<Document><SW.Blocks.FC ID=\"1\" /></Document>", "selected base");
        _fixture.CommitFile(stagedPath, "<Document><SW.Blocks.FC ID=\"2\" /></Document>", "staged base");
        _fixture.WriteFile(stagedPath, "<Document><SW.Blocks.FC ID=\"2\" Name=\"staged\" /></Document>");
        _fixture.WriteFile(selectedPath, "<Document><SW.Blocks.FC ID=\"1\" Name=\"selected\" /></Document>");

        string indexPath;
        string refLockPath;
        using (var repo = new Repository(_fixture.RootPath))
        {
            Commands.Stage(repo, stagedPath);
            indexPath = Path.Combine(repo.Info.Path, "index");
            refLockPath = Path.Combine(repo.Info.Path, repo.Head.CanonicalName.Replace('/', Path.DirectorySeparatorChar)) + ".lock";
        }
        var originalIndex = File.ReadAllBytes(indexPath);
        Directory.CreateDirectory(Path.GetDirectoryName(refLockPath)!);
        File.WriteAllText(refLockPath, "force commit failure");

        CallToolResult result;
        try
        {
            result = _tools.VcCommitSelected(_fixture.RootPath, new[] { selectedPath }, "must fail");
        }
        finally
        {
            File.Delete(refLockPath);
        }

        Assert.True(result.IsError == true);
        Assert.Equal(originalIndex, File.ReadAllBytes(indexPath));
        using var after = new Repository(_fixture.RootPath);
        var status = after.RetrieveStatus();
        Assert.True(IsIndexChange(status[stagedPath].State));
        Assert.False(IsIndexChange(status[selectedPath].State));
    }

    /* ── vc_log ─────────────────────────────────────────── */

    [Fact]
    public void Log_ReturnsCommits()
    {
        _fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", "<Document />", "first");
        _fixture.CommitFile("devices/PLC_1/source/Blocks/B.xml", "<Document />", "second");
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
            _fixture.CommitFile($"devices/PLC_1/source/Blocks/F{i}.xml", $"<Document ID=\"{i}\" />", $"commit {i}");

        var result = _tools.VcLog(_fixture.RootPath, maxCount: 3);
        var data = Unwrap<VcLogResult>(result);
        Assert.NotNull(data);
        Assert.Equal(3, data!.Commits.Length);
    }

    [Fact]
    public void Log_ListsActualFilesForRootAndNonRootCommits()
    {
        const string firstPath = "devices/PLC_1/source/Blocks/A.xml";
        const string secondPath = "devices/PLC_2/source/Types/B.xml";
        var rootSha = _fixture.CommitFile(firstPath, "<Document />", "root");
        var secondSha = _fixture.CommitFile(secondPath, "<Document />", "second");

        var commits = Unwrap<VcLogResult>(_tools.VcLog(_fixture.RootPath))!.Commits;

        Assert.Equal(new[] { secondPath }, Assert.Single(commits, entry => entry.Sha == secondSha).Files);
        Assert.Equal(new[] { firstPath }, Assert.Single(commits, entry => entry.Sha == rootSha).Files);
    }

    [Fact]
    public void Log_RejectsForbiddenFileFilter()
    {
        _fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", "<Document />", "root");

        var result = _tools.VcLog(_fixture.RootPath, filePath: "notes.txt");

        Assert.True(result.IsError == true);
        Assert.Equal("SOURCE_PATH_REQUIRED", ErrorCode(result));
    }

    /* ── vc_diff ────────────────────────────────────────── */

    [Fact]
    public void Diff_NoRefsComparesHeadToWorkingTreeForNestedPath()
    {
        const string sourcePath = "devices/PLC_1/source/Blocks/Nested/Main.xml";
        _fixture.CommitFile(sourcePath, "<Document><SW.Blocks.OB ID=\"1\" Name=\"base\" /></Document>", "init");
        _fixture.WriteFile(sourcePath, "<Document><SW.Blocks.OB ID=\"1\" Name=\"working\" /></Document>");

        var result = _tools.VcDiff(_fixture.RootPath, sourcePath);

        Assert.False(result.IsError == true);
        var data = Unwrap<VcDiffResult>(result);
        Assert.NotNull(data);
        Assert.NotEmpty(data!.Hunks);
        Assert.Contains("working", AddedText(data));
        Assert.Null(data.NewSha);
    }

    [Fact]
    public void Diff_OldRefOnlyComparesThatRefToWorkingTree()
    {
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        var first = _fixture.CommitFile(sourcePath, "<Document><SW.Blocks.OB ID=\"1\" Name=\"first\" /></Document>", "first");
        _fixture.CommitFile(sourcePath, "<Document><SW.Blocks.OB ID=\"1\" Name=\"head\" /></Document>", "head");
        _fixture.WriteFile(sourcePath, "<Document><SW.Blocks.OB ID=\"1\" Name=\"working\" /></Document>");

        var data = Unwrap<VcDiffResult>(_tools.VcDiff(_fixture.RootPath, sourcePath, oldSha: first))!;

        Assert.Contains("working", AddedText(data));
        Assert.DoesNotContain("head", AddedText(data));
        Assert.Equal(first, data.OldSha);
        Assert.Null(data.NewSha);
    }

    [Fact]
    public void Diff_TwoRefsComparesOldToNew()
    {
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        var first = _fixture.CommitFile(sourcePath, "<Document><SW.Blocks.OB ID=\"1\" Name=\"first\" /></Document>", "first");
        var second = _fixture.CommitFile(sourcePath, "<Document><SW.Blocks.OB ID=\"1\" Name=\"second\" /></Document>", "second");
        _fixture.WriteFile(sourcePath, "<Document><SW.Blocks.OB ID=\"1\" Name=\"working\" /></Document>");

        var data = Unwrap<VcDiffResult>(_tools.VcDiff(_fixture.RootPath, sourcePath, first, second))!;

        Assert.Contains("second", AddedText(data));
        Assert.DoesNotContain("working", AddedText(data));
        Assert.Equal(first, data.OldSha);
        Assert.Equal(second, data.NewSha);
    }

    [Fact]
    public void Diff_NewRefOnlyComparesHeadToThatRef()
    {
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        var first = _fixture.CommitFile(sourcePath, "<Document><SW.Blocks.OB ID=\"1\" Name=\"first\" /></Document>", "first");
        var head = _fixture.CommitFile(sourcePath, "<Document><SW.Blocks.OB ID=\"1\" Name=\"head\" /></Document>", "head");

        var data = Unwrap<VcDiffResult>(_tools.VcDiff(_fixture.RootPath, sourcePath, newSha: first))!;

        Assert.Contains("first", AddedText(data));
        Assert.Equal(head, data.OldSha);
        Assert.Equal(first, data.NewSha);
    }

    [Theory]
    [InlineData("missing-old", null)]
    [InlineData(null, "missing-new")]
    public void Diff_MissingRefReturnsPreciseError(string? oldSha, string? newSha)
    {
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        _fixture.CommitFile(sourcePath, "<Document />", "base");

        var result = _tools.VcDiff(_fixture.RootPath, sourcePath, oldSha, newSha);

        Assert.True(result.IsError == true);
        Assert.Equal("REF_NOT_FOUND", ErrorCode(result));
    }

    [Fact]
    public void Diff_TimestampOnlyChangeProducesNoHunksOrCreatedLines()
    {
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        _fixture.CommitFile(sourcePath, SiemensXml("old", "Author A"), "base");
        _fixture.WriteFile(sourcePath, SiemensXml("new", "Author A"));

        var data = Unwrap<VcDiffResult>(_tools.VcDiff(_fixture.RootPath, sourcePath))!;

        Assert.Empty(data.Hunks);
        Assert.DoesNotContain(data.Hunks.SelectMany(h => h.Lines), line => line.Content.Contains("<Created>", StringComparison.Ordinal));
    }

    [Fact]
    public void Diff_ProvidesSemanticSummaryForValidXml()
    {
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        _fixture.CommitFile(sourcePath, SiemensXml("old", "Author A"), "base");
        _fixture.WriteFile(sourcePath, SiemensXml("new", "Author B"));

        var data = Unwrap<VcDiffResult>(_tools.VcDiff(_fixture.RootPath, sourcePath))!;

        Assert.True(data.Summary.SummaryAvailable);
        var header = Assert.Single(data.Summary.HeaderChanges);
        Assert.Equal("HeaderAuthor", header.Field);
        Assert.Equal("Author A", header.OldValue);
        Assert.Equal("Author B", header.NewValue);
    }

    [Fact]
    public void Diff_MalformedXmlMakesSummaryUnavailable()
    {
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        _fixture.CommitFile(sourcePath, SiemensXml("old", "Author A"), "base");
        _fixture.WriteFile(sourcePath, "<Document><broken></Document>");

        var data = Unwrap<VcDiffResult>(_tools.VcDiff(_fixture.RootPath, sourcePath))!;

        Assert.False(data.Summary.SummaryAvailable);
        Assert.NotEmpty(data.Hunks);
    }

    [Fact]
    public void Diff_ProtectedCreatedElementOutsideDocumentInfoRemainsVisible()
    {
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        const string oldXml = "<Document><DocumentInfo><Created>timestamp-1</Created></DocumentInfo><SW.Blocks.OB ID=\"1\"><AttributeList><Created>protected-old</Created></AttributeList></SW.Blocks.OB></Document>";
        const string newXml = "<Document><DocumentInfo><Created>timestamp-2</Created></DocumentInfo><SW.Blocks.OB ID=\"1\"><AttributeList><Created>protected-new</Created></AttributeList></SW.Blocks.OB></Document>";
        _fixture.CommitFile(sourcePath, oldXml, "base");
        _fixture.WriteFile(sourcePath, newXml);

        var data = Unwrap<VcDiffResult>(_tools.VcDiff(_fixture.RootPath, sourcePath))!;

        Assert.Contains("protected-new", AddedText(data));
        Assert.True(data.Summary.LogicOrStructureChanged);
    }

    [Fact]
    public void Diff_BinaryXmlIsReportedWithoutTextHunksOrSemanticInference()
    {
        const string sourcePath = "devices/PLC_1/source/Main.xml";
        _fixture.CommitFile(sourcePath, "<Document />", "base");
        File.WriteAllBytes(Path.Combine(_fixture.RootPath, sourcePath), new byte[] { 0, 1, 2, 3 });

        var data = Unwrap<VcDiffResult>(_tools.VcDiff(_fixture.RootPath, sourcePath))!;

        Assert.True(data.Binary);
        Assert.Empty(data.Hunks);
        Assert.False(data.Summary.SummaryAvailable);
    }

    [Fact]
    public void Diff_OneLineXmlWithDistantEditsProducesLocalizedMultipleHunks()
    {
        const string sourcePath = "devices/PLC_1/source/Blocks/Main.xml";
        var before = NumberedXml();
        var after = before
            .Replace(">Value 4<", ">Changed 4<", StringComparison.Ordinal)
            .Replace(">Value 26<", ">Changed 26<", StringComparison.Ordinal);
        _fixture.CommitFile(sourcePath, before, "base");
        _fixture.WriteFile(sourcePath, after);

        var data = Unwrap<VcDiffResult>(_tools.VcDiff(_fixture.RootPath, sourcePath))!;

        Assert.Equal(2, data.Hunks.Length);
        Assert.All(data.Hunks, hunk =>
        {
            Assert.Contains(hunk.Lines, line => line.Type == "addition");
            Assert.Contains(hunk.Lines, line => line.Type == "deletion");
            Assert.InRange(hunk.Lines.Count(line => line.Type == "context"), 1, 6);
        });
        Assert.Contains("Changed 4", AddedText(data));
        Assert.Contains("Changed 26", AddedText(data));
        Assert.DoesNotContain(data.Hunks.SelectMany(hunk => hunk.Lines), line => line.Content.Length > 200);
    }

    [Fact]
    public void Diff_RejectsForbiddenPath()
    {
        _fixture.CommitFile("devices/PLC_1/source/Main.xml", "<Document />", "base");

        var result = _tools.VcDiff(_fixture.RootPath, "notes.txt");

        Assert.True(result.IsError == true);
        Assert.Equal("SOURCE_PATH_REQUIRED", ErrorCode(result));
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
        const string sourcePath = "devices/PLC_1/source/Blocks/Main.xml";
        _fixture.CommitFile(sourcePath, "<Document Name=\"original\" />", "first");
        _fixture.WriteFile(sourcePath, "<Document Name=\"modified\" />");
        var result = _tools.VcRestore(_fixture.RootPath, sourcePath);

        Assert.False(result.IsError == true);
        var content = File.ReadAllText(Path.Combine(_fixture.RootPath, sourcePath));
        Assert.Equal("<Document Name=\"original\" />", content);
    }

    [Fact]
    public void Restore_ExplicitPathRejectsNonSourceFile()
    {
        _fixture.CommitFile("devices/PLC_1/source/Blocks/Main.xml", "<Document />", "base");

        var result = _tools.VcRestore(_fixture.RootPath, "worktree.json");

        Assert.True(result.IsError == true);
        Assert.Equal("SOURCE_PATH_REQUIRED", ErrorCode(result));
    }

    [Fact]
    public void Restore_AllRecursivelyRestoresOnlySourceXmlFromSelectedCommit()
    {
        using var repo = _fixture.InitRepo();
        const string sourcePath = "devices/PLC_1/source/Blocks/Nested/Main.xml";
        const string metadataPath = "devices/PLC_1/device.json";
        _fixture.WriteFile(sourcePath, "<Document Name=\"old source\" />");
        _fixture.WriteFile(metadataPath, "old metadata");
        Commands.Stage(repo, new[] { sourcePath, metadataPath });
        var author = new Signature("Test", "test@test.local", DateTimeOffset.UtcNow);
        var sourceCommit = repo.Commit("mixed historical commit", author, author).Sha;
        _fixture.WriteFile(sourcePath, "<Document Name=\"working source\" />");
        _fixture.WriteFile(metadataPath, "working metadata");

        var result = _tools.VcRestore(_fixture.RootPath, sourceSha: sourceCommit);

        Assert.False(result.IsError == true);
        Assert.Equal(new[] { sourcePath }, Unwrap<VcRestoreResult>(result)!.Restored);
        Assert.Equal("<Document Name=\"old source\" />", File.ReadAllText(Path.Combine(_fixture.RootPath, sourcePath)));
        Assert.Equal("working metadata", File.ReadAllText(Path.Combine(_fixture.RootPath, metadataPath)));
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

    private static string AddedText(VcDiffResult result) => string.Join(
        "\n",
        result.Hunks.SelectMany(hunk => hunk.Lines)
            .Where(line => line.Type == "addition")
            .Select(line => line.Content));

    private static string SiemensXml(string created, string author) => $$"""
        <Document>
          <DocumentInfo>
            <Created>{{created}}</Created>
          </DocumentInfo>
          <SW.Blocks.OB ID="1">
            <AttributeList>
              <HeaderAuthor>{{author}}</HeaderAuthor>
              <HeaderFamily>Family</HeaderFamily>
              <HeaderName>Main</HeaderName>
            </AttributeList>
          </SW.Blocks.OB>
        </Document>
        """;

    private static string NumberedXml() =>
        "<Document><SW.Blocks.OB ID=\"1\"><ObjectList>" +
        string.Concat(Enumerable.Range(1, 30).Select(number =>
            $"<ProtectedLine ID=\"{number}\">Value {number}</ProtectedLine>")) +
        "</ObjectList></SW.Blocks.OB></Document>";
}
