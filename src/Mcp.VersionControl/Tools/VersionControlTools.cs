using System.ComponentModel;
using Mcp.VersionControl.Git;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.VersionControl.Tools;

/// <summary>
/// MCP tool surface for mcp-versioncontrol (buildnote/plan/version-control.md).
/// Failures are normal tool results with isError=true + { code, message, remediation }.
/// </summary>
[McpServerToolType]
public sealed class VersionControlTools
{
    [McpServerTool(Name = "vc_init")]
    [Description("Initialize a git repository at the given path. Safe to re-run on an already-initialised repo (idempotent). Writes a .gitignore for common PLC artifacts.")]
    public CallToolResult VcInit(
        [Description("Directory to initialise as a git repository (e.g. an export root).")] string repoPath)
        => Invoke(() => RepositoryService.Init(repoPath));

    [McpServerTool(Name = "vc_status")]
    [Description("Show working-tree status: staged, unstaged, modified, added, deleted, and untracked files relative to HEAD.")]
    public CallToolResult VcStatus(
        [Description("Path to the git repository.")] string repoPath)
        => Invoke(() => RepositoryService.Status(repoPath));

    [McpServerTool(Name = "vc_add")]
    [Description("Stage one or more files. When paths is omitted or empty, stages all changes (equivalent to 'git add -A').")]
    public CallToolResult VcAdd(
        [Description("Path to the git repository.")] string repoPath,
        [Description("File path(s) relative to repo root to stage. Omit or pass empty to stage all.")] string[]? paths = null)
        => Invoke(() => RepositoryService.Add(repoPath, paths));

    [McpServerTool(Name = "vc_commit")]
    [Description("Commit staged changes with a message. Returns the commit SHA. Requires at least one staged change.")]
    public CallToolResult VcCommit(
        [Description("Path to the git repository.")] string repoPath,
        [Description("Commit message.")] string message,
        [Description("Optional author string in 'Name <email>' format. Falls back to git config or 'PLC Assistant <assistant@plc-assistant.local>'.")] string? author = null)
        => Invoke(() => RepositoryService.Commit(repoPath, message, author));

    [McpServerTool(Name = "vc_log")]
    [Description("Show commit history. Default last 20 commits; max 100. Optionally filter to commits touching a single file.")]
    public CallToolResult VcLog(
        [Description("Path to the git repository.")] string repoPath,
        [Description("Maximum number of commits (default 20, max 100).")] int? maxCount = null,
        [Description("Optional: filter log to commits touching this file path.")] string? filePath = null)
        => Invoke(() => RepositoryService.Log(repoPath, maxCount, filePath));

    [McpServerTool(Name = "vc_diff")]
    [Description("Show diff of a working-tree file vs HEAD, or between two arbitrary commits. Returns structured hunks with line-level changes.")]
    public CallToolResult VcDiff(
        [Description("Path to the git repository.")] string repoPath,
        [Description("File path relative to repo root.")] string filePath,
        [Description("Old commit SHA; defaults to HEAD when newSha is given, else working tree.")] string? oldSha = null,
        [Description("New commit SHA; defaults to working tree (or HEAD when oldSha is given).")] string? newSha = null)
        => Invoke(() => RepositoryService.Diff(repoPath, filePath, oldSha, newSha));

    [McpServerTool(Name = "vc_snapshot")]
    [Description("Stage all changes and commit with an auto-generated message. Intended as a pre-destructive-operation checkpoint. Returns the commit SHA.")]
    public CallToolResult VcSnapshot(
        [Description("Path to the git repository.")] string repoPath,
        [Description("Optional commit message. Auto-generated: 'checkpoint before <operation>' if omitted.")] string? message = null)
        => Invoke(() => RepositoryService.Snapshot(repoPath, message));

    [McpServerTool(Name = "vc_restore")]
    [Description("Restore a file (or all files) from a given commit, discarding working-tree changes. DESTRUCTIVE: overwrites local changes unrecoverably.")]
    public CallToolResult VcRestore(
        [Description("Path to the git repository.")] string repoPath,
        [Description("Optional file path to restore; omitting restores all files from the source commit.")] string? filePath = null,
        [Description("Source commit SHA; defaults to HEAD.")] string? sourceSha = null)
        => Invoke(() => RepositoryService.Restore(repoPath, filePath, sourceSha));

    [McpServerTool(Name = "vc_branches")]
    [Description("List local branches. The current branch is marked with isHead=true. Read-only.")]
    public CallToolResult VcBranches(
        [Description("Path to the git repository.")] string repoPath)
        => Invoke(() => RepositoryService.Branches(repoPath));

    [McpServerTool(Name = "vc_checkout")]
    [Description("Switch to a branch (git checkout). Fails if there are uncommitted changes that would be lost.")]
    public CallToolResult VcCheckout(
        [Description("Path to the git repository.")] string repoPath,
        [Description("Branch name to switch to.")] string branchName)
        => Invoke(() => RepositoryService.Checkout(repoPath, branchName));

    [McpServerTool(Name = "vc_config")]
    [Description("Get or set git config values (user.name, user.email, etc.). When value is omitted, reads the current value. When value is provided, sets it in local config.")]
    public CallToolResult VcConfig(
        [Description("Path to the git repository.")] string repoPath,
        [Description("Config key, e.g. 'user.name' or 'user.email'.")] string key,
        [Description("Value to set. Omit to read the current value.")] string? value = null)
        => Invoke(() => RepositoryService.Config(repoPath, key, value));

    private static CallToolResult Invoke(Func<object> action)
    {
        try
        {
            return ToolJson.Ok(action());
        }
        catch (VcInternalException ex)
        {
            return ToolJson.Fail(ex.Code, ex.Message, ex.Remediation);
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            return ToolJson.Fail("GIT_ERROR", ex.Message);
        }
        catch (Exception ex)
        {
            return ToolJson.Fail("UNEXPECTED_ERROR", ex.Message);
        }
    }
}
