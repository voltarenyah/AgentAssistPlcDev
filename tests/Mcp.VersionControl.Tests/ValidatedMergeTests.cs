using System;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using Mcp.VersionControl.Git;
using Mcp.VersionControl.Tools;
using Xunit;

namespace Mcp.VersionControl.Tests;

public sealed class ValidatedMergeTests
{
    [Fact]
    public void MergeValidatedPublishesMergeCommitAndEvidenceTag()
    {
        using var fixture = Fixture.Create();
        var preview = RepositoryService.PreviewMerge(fixture.Root, "feature-a");
        Assert.False(preview.HasConflicts);
        var evidence = new VcValidationEvidence(
            "1.0",
            "feature-merge",
            fixture.MasterSha,
            "wb-1",
            "wt-1",
            DateTimeOffset.UtcNow.ToString("O"),
            "Studio user",
            true,
            Array.Empty<VcDeviceValidation>());

        var result = RepositoryService.MergeValidated(new VcValidatedMergeRequest(
            fixture.Root,
            "feature-a",
            fixture.MasterSha,
            fixture.FeatureSha,
            preview.CandidateTreeSha!,
            evidence));

        Assert.True(result.Merged);
        using var repo = new Repository(fixture.Root);
        var merge = repo.Head.Tip;
        Assert.Equal(result.Sha, merge.Sha);
        Assert.Equal(2, merge.Parents.Count());
        Assert.Equal(preview.CandidateTreeSha, merge.Tree.Sha, ignoreCase: true);
        Assert.NotNull(repo.Tags[ValidationTagStore.TagName(merge.Sha)]);
        Assert.Equal(result.Sha, result.Evidence.CommitSha);
    }

    [Fact]
    public void MergeValidatedRejectsWhenTargetMovedAfterValidation()
    {
        using var fixture = Fixture.Create();
        var preview = RepositoryService.PreviewMerge(fixture.Root, "feature-a");
        Fixture.CommitFile(fixture.Root, "devices/PLC_1/source/Blocks/Moved.xml", "<Document />", "moved");

        var error = Assert.Throws<VcInternalException>(() => RepositoryService.MergeValidated(new VcValidatedMergeRequest(
            fixture.Root,
            "feature-a",
            fixture.MasterSha,
            fixture.FeatureSha,
            preview.CandidateTreeSha!,
            Evidence(fixture.MasterSha))));

        Assert.Equal("BRANCH_MOVED", error.Code);
    }

    [Fact]
    public void MergeValidatedIgnoresWorkbenchBookkeepingFiles()
    {
        using var fixture = Fixture.Create();
        var preview = RepositoryService.PreviewMerge(fixture.Root, "feature-a");
        Fixture.WriteFile(fixture.Root, "tasks.json", "[]");
        Fixture.WriteFile(fixture.Root, "hardware/staging/project.aml", "<CAEXFile />");
        Fixture.WriteFile(fixture.Root, "devices/PLC_1/staging/export.xml", "<Document />");

        var result = RepositoryService.MergeValidated(new VcValidatedMergeRequest(
            fixture.Root,
            "feature-a",
            fixture.MasterSha,
            fixture.FeatureSha,
            preview.CandidateTreeSha!,
            Evidence(fixture.MasterSha)));

        Assert.True(result.Merged);
    }

    [Fact]
    public void MergeValidatedDirtyErrorListsOffendingPaths()
    {
        using var fixture = Fixture.Create();
        var preview = RepositoryService.PreviewMerge(fixture.Root, "feature-a");
        Fixture.WriteFile(fixture.Root, "hardware/project.aml", "<CAEXFile />");

        var error = Assert.Throws<VcInternalException>(() => RepositoryService.MergeValidated(new VcValidatedMergeRequest(
            fixture.Root,
            "feature-a",
            fixture.MasterSha,
            fixture.FeatureSha,
            preview.CandidateTreeSha!,
            Evidence(fixture.MasterSha))));

        Assert.Equal("DIRTY_WORKTREE", error.Code);
        Assert.Contains("hardware/project.aml", error.Message);
        Assert.NotNull(error.Remediation);
    }

    [Fact]
    public void MergeValidatedToolAcceptsFlatArgumentsLikeEveryOtherTool()
    {
        // Regression: the tool used to take a single complex `request` parameter, but MCP
        // callers send flat named arguments, so the SDK binding failed before the tool ran
        // ("missing a value for the required parameter 'request'") and the merge appeared stuck.
        using var fixture = Fixture.Create();
        var preview = RepositoryService.PreviewMerge(fixture.Root, "feature-a");
        var tools = new VersionControlTools();

        var result = tools.VcMergeValidated(
            fixture.Root,
            "feature-a",
            fixture.MasterSha,
            fixture.FeatureSha,
            preview.CandidateTreeSha!,
            Evidence(fixture.MasterSha));

        Assert.False(result.IsError == true);
        using var repo = new Repository(fixture.Root);
        Assert.Equal(2, repo.Head.Tip.Parents.Count());
    }

    private static VcValidationEvidence Evidence(string commitSha) => new(
        "1.0",
        "feature-merge",
        commitSha,
        "wb-1",
        "wt-1",
        DateTimeOffset.UtcNow.ToString("O"),
        "Studio user",
        true,
        Array.Empty<VcDeviceValidation>());

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, string masterSha, string featureSha)
        {
            Root = root;
            MasterSha = masterSha;
            FeatureSha = featureSha;
        }

        public string Root { get; }
        public string MasterSha { get; }
        public string FeatureSha { get; }

        public static Fixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "McpVcValidatedMerge", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Repository.Init(root);
            var masterSha = CommitFile(root, "devices/PLC_1/source/Blocks/Main.xml", "<Document><Block>Main</Block></Document>", "base");
            using (var repo = new Repository(root))
            {
                repo.CreateBranch("feature-a", repo.Head.Tip);
                Commands.Checkout(repo, "feature-a");
            }
            var featureSha = CommitFile(root, "devices/PLC_1/source/Blocks/Feature.xml", "<Document><Block>Feature</Block></Document>", "feature");
            using (var repo = new Repository(root))
            {
                Commands.Checkout(repo, "master");
            }
            return new Fixture(root, masterSha, featureSha);
        }

        public static string CommitFile(string root, string path, string content, string message)
        {
            using var repo = new Repository(root);
            var fullPath = WriteFile(root, path, content);
            Commands.Stage(repo, path);
            var signature = new Signature("Test", "test@test.local", DateTimeOffset.UtcNow);
            return repo.Commit(message, signature, signature).Sha;
        }

        public static string WriteFile(string root, string path, string content)
        {
            var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for Windows file handles.
            }
        }
    }
}
