using Mcp.VersionControl.Git;
using Mcp.VersionControl.Tools;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
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
            RemoveReparsePointsAndClearFileAttributes(root);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SharedRepositorySupportsLinkedWorktreeCommitAndMergeLifecycle()
    {
        const string seedPath = "devices/PLC_1/source/Seed.xml";
        const string featureSourcePath = "devices/PLC_1/source/Feature.xml";
        var workbenchRoot = Path.Combine(root, "workbench");
        var masterPath = Path.Combine(workbenchRoot, "worktrees", "master");
        var featurePath = Path.Combine(workbenchRoot, "worktrees", "feature-a");

        var init = RepositoryService.InitShared(workbenchRoot, masterPath);

        Assert.Equal(Path.Combine(workbenchRoot, "repository.git"), init.RepositoryPath);
        Assert.Equal(masterPath, init.MasterWorktreePath);
        Assert.True(Directory.Exists(init.RepositoryPath));
        Assert.True(File.Exists(Path.Combine(masterPath, ".git")));

        WriteFile(masterPath, seedPath, "<Document Name=\"Seed\" />");
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
        Assert.Equal(
            "<Document Name=\"Seed\" />",
            File.ReadAllText(Path.Combine(featurePath, seedPath)));

        var listed = RepositoryService.Worktrees(init.RepositoryPath);
        Assert.Contains(listed.Worktrees, item =>
            item.WorktreePath == masterPath && item.Branch == "master");
        Assert.Contains(listed.Worktrees, item =>
            item.WorktreePath == featurePath && item.Branch == "feature-a");

        WriteFile(featurePath, featureSourcePath, "<Document Name=\"Feature\" />");
        RepositoryService.Add(featurePath);
        var featureCommit = RepositoryService.Commit(featurePath, "feature change", null);

        var merge = RepositoryService.Merge(masterPath, "feature-a");

        Assert.True(merge.Merged);
        Assert.Equal("feature-a", merge.SourceBranch);
        Assert.Equal(featureCommit.Sha, merge.SourceSha);
        Assert.False(string.IsNullOrWhiteSpace(merge.Sha));
        Assert.Equal(
            "<Document Name=\"Feature\" />",
            File.ReadAllText(Path.Combine(masterPath, featureSourcePath)));
    }

    [Fact]
    public void StatusAndLogSupportLinkedWorktreeAfterSourceXmlWasCommitted()
    {
        var (repositoryPath, masterPath, firstSha) = CreateSharedRepositoryWithInitialCommit();
        var featurePath = Path.Combine(root, "workbench", "worktrees", "feature-a");
        RepositoryService.AddWorktree(repositoryPath, featurePath, "feature-a", firstSha);

        var sourceDirectory = Path.Combine(featurePath, "devices", "PLC_1", "source", "Blocks");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "A.xml"), "<a>first</a>");
        RepositoryService.Add(featurePath);
        RepositoryService.Commit(featurePath, "import A", null);
        File.WriteAllText(Path.Combine(sourceDirectory, "A.xml"), "<a>second</a>");

        var status = RepositoryService.Status(featurePath);
        var sourcePath = "devices/PLC_1/source/Blocks/A.xml";
        var log = RepositoryService.Log(featurePath, filePath: sourcePath);

        Assert.Contains(status.Entries, entry => entry.FilePath == sourcePath && entry.State == "Modified");
        Assert.Contains(log.Commits, commit => commit.Message == "import A");
    }

    [Fact]
    public void RemoveWorktreeDeletesCheckoutAndUnregistersIt()
    {
        var (repositoryPath, masterPath, firstSha) = CreateSharedRepositoryWithInitialCommit();
        var featurePath = Path.Combine(root, "workbench", "worktrees", "feature-a");
        RepositoryService.AddWorktree(repositoryPath, featurePath, "feature-a", firstSha);
        File.WriteAllText(Path.Combine(featurePath, "dirty.txt"), "uncommitted");

        var removed = RepositoryService.RemoveWorktree(repositoryPath, featurePath);

        Assert.True(removed.Removed);
        Assert.Equal(repositoryPath, removed.RepositoryPath);
        Assert.False(Directory.Exists(featurePath));
        var listed = RepositoryService.Worktrees(repositoryPath);
        Assert.DoesNotContain(listed.Worktrees, item => item.WorktreePath == featurePath);
        Assert.Contains(listed.Worktrees, item => item.WorktreePath == masterPath);
    }

    [Fact]
    public void RemoveWorktreeRejectsPathOutsideWorkbench()
    {
        var (repositoryPath, _, _) = CreateSharedRepositoryWithInitialCommit();
        var outsidePath = Path.Combine(root, "outside", "feature-x");

        var error = Assert.Throws<VcInternalException>(
            () => RepositoryService.RemoveWorktree(repositoryPath, outsidePath));

        Assert.Equal("PATH_OUTSIDE_WORKBENCH", error.Code);
    }

    [Fact]
    public void VersionControlToolsExposeWorktreeRemoval()
    {
        var tools = new VersionControlTools();
        var workbenchRoot = Path.Combine(root, "workbench");
        var masterPath = Path.Combine(workbenchRoot, "worktrees", "master");
        var featurePath = Path.Combine(workbenchRoot, "worktrees", "feature-a");

        var init = Unwrap<VcSharedInitResult>(tools.VcInitShared(workbenchRoot, masterPath));
        Assert.NotNull(init);
        File.WriteAllText(Path.Combine(masterPath, "seed.txt"), "seed");
        tools.VcAdd(masterPath);
        var commit = Unwrap<VcCommitResult>(tools.VcCommit(masterPath, "initial"));
        Assert.NotNull(commit);
        var addCall = tools.VcAddWorktree(init!.RepositoryPath, featurePath, "feature-a", commit!.Sha);
        Assert.False(addCall.IsError == true);

        var removeCall = tools.VcRemoveWorktree(init.RepositoryPath, featurePath);

        Assert.False(removeCall.IsError == true);
        Assert.True(Unwrap<VcWorktreeRemoveResult>(removeCall)!.Removed);
        Assert.False(Directory.Exists(featurePath));
        var remaining = Unwrap<VcWorktreeListResult>(tools.VcWorktrees(init.RepositoryPath))!.Worktrees;
        Assert.Single(remaining);
        Assert.Equal(masterPath, remaining[0].WorktreePath);
    }

    [Fact]
    public void SharedInitUsesInternalExcludesAndDoesNotCreateGitIgnore()
    {
        var workbenchRoot = Path.Combine(root, "workbench");
        var masterPath = Path.Combine(workbenchRoot, "worktrees", "master");

        var result = RepositoryService.InitShared(workbenchRoot, masterPath);

        Assert.False(File.Exists(Path.Combine(masterPath, ".gitignore")));
        var exclude = File.ReadAllText(Path.Combine(result.RepositoryPath, "info", "exclude"));
        Assert.Contains("worktree.json", exclude);
        Assert.Contains("devices/*/device.json", exclude);
        Assert.Contains("devices/*/staging/", exclude);
        Assert.Contains("devices/*/plc-knowledge.db*", exclude);
        Assert.Contains(".automation/", exclude);
        Assert.Contains("sessionexport/", exclude);
    }

    [Fact]
    public void InitSharedPreservesExistingExcludeContentAndAppendsOnlyMissingRules()
    {
        var workbenchRoot = Path.Combine(root, "workbench");
        var masterPath = Path.Combine(workbenchRoot, "worktrees", "master");
        var result = RepositoryService.InitShared(workbenchRoot, masterPath);
        var excludePath = Path.Combine(result.RepositoryPath, "info", "exclude");
        const string userContent = "# user rules\r\n*.custom\r\ndevices/*/staging/\r\n";
        File.WriteAllText(excludePath, userContent);

        RepositoryService.InitShared(workbenchRoot, masterPath);
        var afterFirstRetry = File.ReadAllText(excludePath);
        RepositoryService.InitShared(workbenchRoot, masterPath);
        var afterSecondRetry = File.ReadAllText(excludePath);

        Assert.StartsWith(userContent, afterFirstRetry);
        Assert.Equal(1, CountLines(afterFirstRetry, "worktree.json"));
        Assert.Equal(1, CountLines(afterFirstRetry, "devices/*/device.json"));
        Assert.Equal(1, CountLines(afterFirstRetry, "devices/*/staging/"));
        Assert.Equal(1, CountLines(afterFirstRetry, "devices/*/plc-knowledge.db*"));
        Assert.Equal(1, CountLines(afterFirstRetry, ".automation/"));
        Assert.Equal(1, CountLines(afterFirstRetry, "sessionexport/"));
        Assert.Equal(afterFirstRetry, afterSecondRetry);
    }

    [Fact]
    public void StatusReturnsOnlyNonIgnoredPlcSourceXml()
    {
        var (repositoryPath, masterPath, _) = CreateSharedRepositoryWithInitialCommit();
        var sourceDirectory = Path.Combine(masterPath, "devices", "PLC_1", "source", "Blocks");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(Path.Combine(masterPath, "devices", "PLC_1", "staging"));
        File.WriteAllText(Path.Combine(masterPath, "worktree.json"), "{}");
        File.WriteAllText(Path.Combine(masterPath, "devices", "PLC_1", "device.json"), "{}");
        File.WriteAllText(Path.Combine(masterPath, "devices", "PLC_1", "staging", "Temp.xml"), "<Document />");
        File.WriteAllText(Path.Combine(masterPath, "devices", "PLC_1", "plc-knowledge.db-wal"), "runtime");
        File.WriteAllText(Path.Combine(sourceDirectory, "Main.xml"), "<Document />");
        File.WriteAllText(Path.Combine(sourceDirectory, "Ignored.xml"), "<Document />");
        File.WriteAllText(Path.Combine(sourceDirectory, "notes.txt"), "not PLC source");
        File.WriteAllText(Path.Combine(masterPath, "notes.txt"), "not PLC source");
        File.AppendAllText(
            Path.Combine(repositoryPath, "info", "exclude"),
            $"devices/PLC_1/source/Blocks/Ignored.xml{Environment.NewLine}");

        var status = RepositoryService.Status(masterPath);

        var entry = Assert.Single(status.Entries);
        Assert.Equal("devices/PLC_1/source/Blocks/Main.xml", entry.FilePath);
        Assert.Equal("Untracked", entry.State);
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
    public void MergeAllowsIgnoredWorkbenchRuntimeArtifacts()
    {
        const string featureSourcePath = "devices/PLC_1/source/Feature.xml";
        var (repositoryPath, masterPath, firstSha) = CreateSharedRepositoryWithInitialCommit();
        var featurePath = Path.Combine(root, "workbench", "worktrees", "feature-a");
        RepositoryService.AddWorktree(repositoryPath, featurePath, "feature-a", firstSha);
        WriteFile(featurePath, featureSourcePath, "<Document Name=\"Feature\" />");
        RepositoryService.Add(featurePath);
        RepositoryService.Commit(featurePath, "feature", null);

        var deviceRoot = Path.Combine(masterPath, "devices", "PLC_1");
        Directory.CreateDirectory(Path.Combine(deviceRoot, "staging"));
        File.WriteAllText(Path.Combine(deviceRoot, "staging", "metadata.json"), "{}");
        File.WriteAllText(Path.Combine(deviceRoot, "plc-knowledge.db"), "runtime");

        var result = RepositoryService.Merge(masterPath, "feature-a");

        Assert.True(result.Merged);
        Assert.True(File.Exists(Path.Combine(masterPath, featureSourcePath)));
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
    public void InitSharedRejectsReparsePointInWorkbenchRoot()
    {
        var target = Path.Combine(root, "real-workbench");
        var linkedRoot = Path.Combine(root, "linked-workbench");
        Directory.CreateDirectory(target);
        CreateDirectoryLink(linkedRoot, target);

        var error = Assert.Throws<VcInternalException>(() =>
            RepositoryService.InitShared(
                linkedRoot,
                Path.Combine(linkedRoot, "worktrees", "master")));

        Assert.Equal("REPARSE_POINT_NOT_ALLOWED", error.Code);
        Assert.False(Directory.Exists(Path.Combine(target, "repository.git")));
    }

    [Fact]
    public void InitSharedRejectsReparsePointAtRepositoryPath()
    {
        var workbenchRoot = Path.Combine(root, "workbench");
        var repositoryTarget = Path.Combine(root, "foreign-repository");
        Directory.CreateDirectory(workbenchRoot);
        Directory.CreateDirectory(repositoryTarget);
        CreateDirectoryLink(
            Path.Combine(workbenchRoot, "repository.git"),
            repositoryTarget);

        var error = Assert.Throws<VcInternalException>(() =>
            RepositoryService.InitShared(
                workbenchRoot,
                Path.Combine(workbenchRoot, "worktrees", "master")));

        Assert.Equal("REPARSE_POINT_NOT_ALLOWED", error.Code);
    }

    [Fact]
    public void InitSharedRejectsReparsePointTraversedByMasterPath()
    {
        var workbenchRoot = Path.Combine(root, "workbench");
        var worktreesTarget = Path.Combine(root, "foreign-worktrees");
        Directory.CreateDirectory(workbenchRoot);
        Directory.CreateDirectory(worktreesTarget);
        CreateDirectoryLink(
            Path.Combine(workbenchRoot, "worktrees"),
            worktreesTarget);

        var error = Assert.Throws<VcInternalException>(() =>
            RepositoryService.InitShared(
                workbenchRoot,
                Path.Combine(workbenchRoot, "worktrees", "master")));

        Assert.Equal("REPARSE_POINT_NOT_ALLOWED", error.Code);
        Assert.False(Directory.Exists(Path.Combine(worktreesTarget, "master")));
    }

    [Fact]
    public void AddWorktreeRejectsReparsePointTraversedByCheckoutPath()
    {
        var (repositoryPath, _, firstSha) = CreateSharedRepositoryWithInitialCommit();
        var checkoutTarget = Path.Combine(root, "foreign-worktrees");
        var linkedParent = Path.Combine(root, "workbench", "linked-worktrees");
        Directory.CreateDirectory(checkoutTarget);
        CreateDirectoryLink(linkedParent, checkoutTarget);

        var error = Assert.Throws<VcInternalException>(() =>
            RepositoryService.AddWorktree(
                repositoryPath,
                Path.Combine(linkedParent, "feature-a"),
                "feature-a",
                firstSha));

        Assert.Equal("REPARSE_POINT_NOT_ALLOWED", error.Code);
        Assert.False(Directory.Exists(Path.Combine(checkoutTarget, "feature-a")));
    }

    [Fact]
    public void InitSharedRejectsExistingMasterLinkedToDifferentRepository()
    {
        var foreign = RepositoryService.InitShared(
            root,
            Path.Combine(root, "worktrees", "foreign-master"));
        File.WriteAllText(
            Path.Combine(foreign.MasterWorktreePath, "seed.txt"),
            "seed");
        RepositoryService.Add(foreign.MasterWorktreePath);
        var first = RepositoryService.Commit(
            foreign.MasterWorktreePath,
            "initial",
            null);

        var workbenchRoot = Path.Combine(root, "nested-workbench");
        var masterPath = Path.Combine(workbenchRoot, "worktrees", "master");
        RepositoryService.AddWorktree(
            foreign.RepositoryPath,
            masterPath,
            "foreign-linked-master",
            first.Sha);

        var error = Assert.Throws<VcInternalException>(
            () => RepositoryService.InitShared(workbenchRoot, masterPath));

        Assert.Equal("WORKTREE_REPOSITORY_MISMATCH", error.Code);
        Assert.Contains("repository.git", error.Message);
    }

    [Fact]
    public void VersionControlToolsExposeSharedWorktreeOperations()
    {
        const string seedPath = "devices/PLC_1/source/Seed.xml";
        const string featureSourcePath = "devices/PLC_1/source/Feature.xml";
        var tools = new VersionControlTools();
        var workbenchRoot = Path.Combine(root, "workbench");
        var masterPath = Path.Combine(workbenchRoot, "worktrees", "master");
        var featurePath = Path.Combine(workbenchRoot, "worktrees", "feature-a");

        var initCall = tools.VcInitShared(workbenchRoot, masterPath);
        Assert.False(initCall.IsError == true);
        var init = Unwrap<VcSharedInitResult>(initCall);
        Assert.NotNull(init);

        WriteFile(masterPath, seedPath, "<Document Name=\"Seed\" />");
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

        WriteFile(featurePath, featureSourcePath, "<Document Name=\"Feature\" />");
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

        WriteFile(
            masterPath,
            "devices/PLC_1/source/Seed.xml",
            "<Document Name=\"Seed\" />");
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
        WriteFile(
            featurePath,
            "devices/PLC_1/source/Feature.xml",
            "<Document Name=\"Feature\" />");
        RepositoryService.Add(featurePath);
        RepositoryService.Commit(featurePath, "feature change", null);
        return result;
    }

    private static void WriteFile(string worktreePath, string relativePath, string content)
    {
        var fullPath = Path.Combine(worktreePath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
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

    private static int CountLines(string text, string expected) =>
        text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Count(line => line == expected);

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start cmd.exe to create a junction.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Could not create junction. stdout: {output} stderr: {error}");
    }

    private static void RemoveReparsePointsAndClearFileAttributes(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            foreach (var entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (entry.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        Directory.Delete(entry.FullName);
                    }
                    else
                    {
                        File.Delete(entry.FullName);
                    }
                }
                else if (entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry.FullName);
                }
                else
                {
                    File.SetAttributes(entry.FullName, FileAttributes.Normal);
                }
            }
        }
    }
}
