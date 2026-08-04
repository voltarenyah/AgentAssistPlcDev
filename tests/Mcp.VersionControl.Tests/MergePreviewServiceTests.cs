using System;
using System.Linq;
using LibGit2Sharp;
using Mcp.VersionControl.Git;
using Xunit;

namespace Mcp.VersionControl.Tests;

public sealed class MergePreviewServiceTests
{
    [Fact]
    public void PreviewReturnsCandidateTreeAndPreservesRefs()
    {
        using var fixture = MergeFixture.CreateDisjointFeature();
        var beforeMaster = fixture.MasterSha;
        var beforeFeature = fixture.FeatureSha;

        var preview = RepositoryService.PreviewMerge(fixture.MasterPath, "feature-a");

        Assert.False(preview.HasConflicts);
        Assert.Equal(beforeMaster, preview.TargetSha);
        Assert.Equal(beforeFeature, preview.SourceSha);
        Assert.NotEmpty(preview.MergeBaseSha);
        Assert.NotNull(preview.CandidateTreeSha);
        Assert.NotEmpty(preview.CandidateTreeSha!);
        Assert.Contains(preview.FeaturePaths, path => path.EndsWith("Feature.xml", StringComparison.Ordinal));
        Assert.Contains(preview.Objects, item => item.FilePath.EndsWith("Feature.xml", StringComparison.Ordinal));
        Assert.Equal(beforeMaster, fixture.ReadBranchSha("master"));
        Assert.Equal(beforeFeature, fixture.ReadBranchSha("feature-a"));
    }

    [Fact]
    public void PreviewReportsSameFileConflictWithoutMovingRefs()
    {
        using var fixture = MergeFixture.CreateConflictingFeature();

        var preview = RepositoryService.PreviewMerge(fixture.MasterPath, "feature-a");

        Assert.True(preview.HasConflicts);
        Assert.Contains("devices/PLC_1/source/Blocks/Main.xml", preview.ConflictPaths);
        Assert.Null(preview.CandidateTreeSha);
        Assert.Empty(preview.Objects);
        Assert.Equal(fixture.MasterSha, fixture.ReadBranchSha("master"));
        Assert.Equal(fixture.FeatureSha, fixture.ReadBranchSha("feature-a"));
    }

    private sealed class MergeFixture : IDisposable
    {
        private const string MainPath = "devices/PLC_1/source/Blocks/Main.xml";
        private const string FeaturePath = "devices/PLC_1/source/Blocks/Feature.xml";
        private readonly string _root;

        private MergeFixture(string root, string masterSha, string featureSha)
        {
            _root = root;
            MasterSha = masterSha;
            FeatureSha = featureSha;
            MasterPath = root;
        }

        public string MasterPath { get; }
        public string MasterSha { get; }
        public string FeatureSha { get; }

        public static MergeFixture CreateDisjointFeature()
        {
            var root = CreateRepository();
            using var repo = new Repository(root);
            var masterSha = CommitFile(repo, MainPath, "<Document><Block>Main</Block></Document>", "base");
            repo.CreateBranch("feature-a", repo.Head.Tip);
            Commands.Checkout(repo, "feature-a");
            var featureSha = CommitFile(repo, FeaturePath, "<Document><Block>Feature</Block></Document>", "feature");
            Commands.Checkout(repo, "master");
            return new MergeFixture(root, masterSha, featureSha);
        }

        public static MergeFixture CreateConflictingFeature()
        {
            var root = CreateRepository();
            using var repo = new Repository(root);
            _ = CommitFile(repo, MainPath, "<Document><Block>Base</Block></Document>", "base");
            repo.CreateBranch("feature-a", repo.Head.Tip);
            Commands.Checkout(repo, "feature-a");
            var featureSha = CommitFile(repo, MainPath, "<Document><Block>Feature</Block></Document>", "feature");
            Commands.Checkout(repo, "master");
            var masterSha = CommitFile(repo, MainPath, "<Document><Block>Master</Block></Document>", "master");
            return new MergeFixture(root, masterSha, featureSha);
        }

        public string ReadBranchSha(string branchName)
        {
            using var repo = new Repository(_root);
            return repo.Branches[branchName]!.Tip!.Sha;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for Windows file handles.
            }
        }

        private static string CreateRepository()
        {
            var root = Path.Combine(Path.GetTempPath(), "McpVcMergePreview", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Repository.Init(root);
            return root;
        }

        private static string CommitFile(Repository repo, string path, string content, string message)
        {
            var fullPath = Path.Combine(repo.Info.WorkingDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            Commands.Stage(repo, path);
            var signature = new Signature("Test", "test@test.local", DateTimeOffset.UtcNow);
            return repo.Commit(message, signature, signature).Sha;
        }
    }
}
