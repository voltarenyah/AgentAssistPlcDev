using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class WorkbenchPathsTests
{
    [Fact]
    public void DefaultRootUsesAutomationWorkbenchProjectAndSanitizedName()
    {
        var root = WorkbenchPaths.DefaultRoot("Line:1");

        Assert.EndsWith(Path.Combine("AutomationWorkbench", "Project", "Line_1"), root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    public void DefaultRootRejectsNamesWithoutSafeDirectoryIdentity(string name)
    {
        Assert.Throws<WorkbenchPathException>(() => WorkbenchPaths.DefaultRoot(name));
    }

    [Fact]
    public void ResolveWorkbenchCanonicalizesCustomRoot()
    {
        var requested = Path.Combine(Path.GetTempPath(), "workbench-path-tests", ".", "Line1");

        var resolved = WorkbenchPaths.ResolveWorkbench("Line 1", requested);

        Assert.Equal(Path.GetFullPath(requested), resolved);
    }

    [Fact]
    public void ResolveWorktreeResolvesRegisteredRelativePathBelowWorkbench()
    {
        var workbenchRoot = Path.Combine(Path.GetTempPath(), "workbench-path-tests");

        var resolved = WorkbenchPaths.ResolveWorktree(workbenchRoot, Path.Combine("feature", "line-a"));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(workbenchRoot, "worktrees", "feature", "line-a")),
            resolved);
    }

    [Fact]
    public void ResolveDeviceUsesOneTrackedSourceDirectory()
    {
        var context = WorkbenchPaths.ResolveDevice(
            "wb-1", @"D:\wb", "wt-1", "feature-a", "dev-1", "PLC:1");

        Assert.Equal(@"D:\wb\worktrees\feature-a\devices\PLC_1", context.DeviceRoot);
        Assert.Equal(Path.Combine(context.DeviceRoot, "source"), context.SourceRoot);
        Assert.Equal(Path.Combine(context.DeviceRoot, "staging"), context.StagingRoot);
        Assert.Equal(Path.Combine(context.DeviceRoot, "plc-knowledge.db"), context.KnowledgeDbPath);
        Assert.Equal("wb-1", context.WorkbenchId);
        Assert.Equal("wt-1", context.WorktreeId);
        Assert.Equal("dev-1", context.DeviceId);
    }

    [Fact]
    public void ResolveDevicePreservesAllStableIds()
    {
        var context = WorkbenchPaths.ResolveDevice(
            "wb-1", @"D:\wb", "wt-1", "feature-a", "dev-1", "PLC:1");

        Assert.Equal("wb-1", context.WorkbenchId);
        Assert.Equal("wt-1", context.WorktreeId);
        Assert.Equal("dev-1", context.DeviceId);
    }

    [Theory]
    [InlineData(@"..\..\escape.xml")]
    [InlineData(@"\escape.xml")]
    [InlineData(@"D:\escape.xml")]
    public void ResolveRelativeRejectsTraversalAndRootedPaths(string relativePath)
    {
        Assert.Throws<WorkbenchPathException>(() =>
            WorkbenchPaths.ResolveRelative(@"D:\wb\worktrees\master", relativePath));
    }

    [Fact]
    public void ResolveRelativeRejectsExistingReparsePoint()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"workbench-path-tests-{Guid.NewGuid():N}");
        var parent = Path.Combine(testRoot, "parent");
        var outside = Path.Combine(testRoot, "outside");
        var link = Path.Combine(parent, "link");

        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);

        try
        {
            CreateDirectoryLink(link, outside);

            Assert.Throws<WorkbenchPathException>(() =>
                WorkbenchPaths.ResolveRelative(parent, Path.Combine("link", "escape.xml")));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }

            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;

        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Could not create test junction: {process.StandardError.ReadToEnd()}");
    }
}
