using Mcp.VersionControl.Git;
using Mcp.VersionControl.Tools;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using Xunit;

namespace Mcp.VersionControl.Tests;

public sealed class LinkedWorktreeTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "McpVcLinkedWorktreeTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SharedRepositorySupportsLinkedWorktreeCommitAndMergeLifecycle()
    {
        var workbenchRoot = Path.Combine(root, "workbench");
        var masterPath = Path.Combine(workbenchRoot, "worktrees", "master");
        var featurePath = Path.Combine(workbenchRoot, "worktrees", "feature-a");

        var init = RepositoryService.InitShared(workbenchRoot, masterPath);

        Assert.Equal(Path.Combine(workbenchRoot, "repository.git"), init.RepositoryPath);
        Assert.Equal(masterPath, init.MasterWorktreePath);
        Assert.True(Directory.Exists(init.RepositoryPath));
        Assert.True(File.Exists(Path.Combine(masterPath, ".git")));

        File.WriteAllText(Path.Combine(masterPath, "seed.txt"), "seed");
        RepositoryService.Add(masterPath);
        var first = RepositoryService.Commit(masterPath, "initial", null);

        var feature = RepositoryService.AddWorktree(
            init.RepositoryPath,
            featurePath,
            "feature-a",
            first.Sha);

        Assert.Equal("feature-a", feature.Branch);
        Assert.Equal(featurePath, feature.WorktreePath);
        Assert.Equal(first.Sha, feature.Sha);
        Assert.Equal("seed", File.ReadAllText(Path.Combine(featurePath, "seed.txt")));

        var listed = RepositoryService.Worktrees(init.RepositoryPath);
        Assert.Contains(listed.Worktrees, item =>
            item.WorktreePath == masterPath && item.Branch == "master");
        Assert.Contains(listed.Worktrees, item =>
            item.WorktreePath == featurePath && item.Branch == "feature-a");

        File.WriteAllText(Path.Combine(featurePath, "change.txt"), "feature");
        RepositoryService.Add(featurePath);
        var featureCommit = RepositoryService.Commit(featurePath, "feature change", null);

        var merge = RepositoryService.Merge(masterPath, "feature-a");

        Assert.True(merge.Merged);
        Assert.Equal("feature-a", merge.SourceBranch);
        Assert.Equal(featureCommit.Sha, merge.SourceSha);
        Assert.False(string.IsNullOrWhiteSpace(merge.Sha));
        Assert.Equal("feature", File.ReadAllText(Path.Combine(masterPath, "change.txt")));
    }

    [Fact]
    public void InitSharedWritesWorkbenchIgnoreRules()
    {
        var workbenchRoot = Path.Combine(root, "workbench");
        var masterPath = Path.Combine(workbenchRoot, "worktrees", "master");

        RepositoryService.InitShared(workbenchRoot, masterPath);

        var ignore = File.ReadAllText(Path.Combine(masterPath, ".gitignore"));
        Assert.Contains("**/staging/", ignore);
        Assert.Contains("**/plc-knowledge.db", ignore);
        Assert.Contains("**/plc-knowledge.db-*", ignore);
        Assert.Contains(".automation/", ignore);
    }

    [Fact]
    public void MergeRejectsDirtyTargetWorktree()
    {
        var (repositoryPath, masterPath, _) = CreateFeatureWithCommit();
        File.WriteAllText(Path.Combine(masterPath, "dirty.txt"), "dirty");

        var error = Assert.Throws<VcInternalException>(
            () => RepositoryService.Merge(masterPath, "feature-a"));

        Assert.Equal("DIRTY_WORKTREE", error.Code);
        Assert.True(Directory.Exists(repositoryPath));
    }

    [Fact]
    public void AddWorktreeRejectsDuplicateBranch()
    {
        var (repositoryPath, _, firstSha) = CreateSharedRepositoryWithInitialCommit();
        RepositoryService.AddWorktree(
            repositoryPath,
            Path.Combine(root, "workbench", "worktrees", "feature-a"),
            "feature-a",
            firstSha);

        var error = Assert.Throws<VcInternalException>(() =>
            RepositoryService.AddWorktree(
                repositoryPath,
                Path.Combine(root, "workbench", "worktrees", "feature-b"),
                "feature-a",
                firstSha));

        Assert.Equal("BRANCH_EXISTS", error.Code);
    }

    [Fact]
    public void AddWorktreeRejectsDuplicatePath()
    {
        var (repositoryPath, _, firstSha) = CreateSharedRepositoryWithInitialCommit();
        var featurePath = Path.Combine(root, "workbench", "worktrees", "feature-a");
        RepositoryService.AddWorktree(repositoryPath, featurePath, "feature-a", firstSha);

        var error = Assert.Throws<VcInternalException>(() =>
            RepositoryService.AddWorktree(repositoryPath, featurePath, "feature-b", firstSha));

        Assert.Equal("WORKTREE_EXISTS", error.Code);
    }

    [Fact]
    public void InitSharedRejectsMasterOutsideWorkbench()
    {
        var workbenchRoot = Path.Combine(root, "workbench");
        var outsidePath = Path.Combine(root, "outside", "master");

        var error = Assert.Throws<VcInternalException>(
            () => RepositoryService.InitShared(workbenchRoot, outsidePath));

        Assert.Equal("PATH_OUTSIDE_WORKBENCH", error.Code);
        Assert.False(Directory.Exists(Path.Combine(workbenchRoot, "repository.git")));
    }

    [Fact]
    public void AddWorktreeRejectsPathOutsideWorkbench()
    {
        var (repositoryPath, _, firstSha) = CreateSharedRepositoryWithInitialCommit();
        var outsidePath = Path.Combine(root, "outside", "feature-a");

        var error = Assert.Throws<VcInternalException>(() =>
            RepositoryService.AddWorktree(repositoryPath, outsidePath, "feature-a", firstSha));

        Assert.Equal("PATH_OUTSIDE_WORKBENCH", error.Code);
        Assert.False(Directory.Exists(outsidePath));
    }

    [Fact]
    public void VersionControlToolsExposeSharedWorktreeOperations()
    {
        var tools = new VersionControlTools();
        var workbenchRoot = Path.Combine(root, "workbench");
        var masterPath = Path.Combine(workbenchRoot, "worktrees", "master");
        var featurePath = Path.Combine(workbenchRoot, "worktrees", "feature-a");

        var initCall = tools.VcInitShared(workbenchRoot, masterPath);
        Assert.False(initCall.IsError == true);
        var init = Unwrap<VcSharedInitResult>(initCall);
        Assert.NotNull(init);

        File.WriteAllText(Path.Combine(masterPath, "seed.txt"), "seed");
        tools.VcAdd(masterPath);
        var commit = Unwrap<VcCommitResult>(tools.VcCommit(masterPath, "initial"));
        Assert.NotNull(commit);

        var addCall = tools.VcAddWorktree(
            init!.RepositoryPath,
            featurePath,
            "feature-a",
            commit!.Sha);
        Assert.False(addCall.IsError == true);
        Assert.Equal("feature-a", Unwrap<VcWorktreeResult>(addCall)!.Branch);

        var listCall = tools.VcWorktrees(init.RepositoryPath);
        Assert.False(listCall.IsError == true);
        Assert.Equal(2, Unwrap<VcWorktreeListResult>(listCall)!.Worktrees.Length);

        File.WriteAllText(Path.Combine(featurePath, "change.txt"), "feature");
        tools.VcAdd(featurePath);
        tools.VcCommit(featurePath, "feature change");

        var mergeCall = tools.VcMerge(masterPath, "feature-a");
        Assert.False(mergeCall.IsError == true);
        Assert.True(Unwrap<VcMergeResult>(mergeCall)!.Merged);
    }

    private (string RepositoryPath, string MasterPath, string FirstSha)
        CreateSharedRepositoryWithInitialCommit()
    {
        var workbenchRoot = Path.Combine(root, "workbench");
        var masterPath = Path.Combine(workbenchRoot, "worktrees", "master");
        var init = RepositoryService.InitShared(workbenchRoot, masterPath);

        File.WriteAllText(Path.Combine(masterPath, "seed.txt"), "seed");
        RepositoryService.Add(masterPath);
        var first = RepositoryService.Commit(masterPath, "initial", null);
        return (init.RepositoryPath, masterPath, first.Sha);
    }

    private (string RepositoryPath, string MasterPath, string FirstSha)
        CreateFeatureWithCommit()
    {
        var result = CreateSharedRepositoryWithInitialCommit();
        var featurePath = Path.Combine(root, "workbench", "worktrees", "feature-a");
        RepositoryService.AddWorktree(
            result.RepositoryPath,
            featurePath,
            "feature-a",
            result.FirstSha);
        File.WriteAllText(Path.Combine(featurePath, "change.txt"), "feature");
        RepositoryService.Add(featurePath);
        RepositoryService.Commit(featurePath, "feature change", null);
        return result;
    }

    private static T? Unwrap<T>(CallToolResult result)
    {
        var block = result.Content?.FirstOrDefault();
        if (block is not TextContentBlock textBlock || string.IsNullOrEmpty(textBlock.Text))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(textBlock.Text, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        });
    }
}
