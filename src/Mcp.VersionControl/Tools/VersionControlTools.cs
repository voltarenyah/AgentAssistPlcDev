using System.ComponentModel;
using Mcp.VersionControl.Git;
using Mcp.VersionControl.Svn;
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

    [McpServerTool(Name = "vc_init_shared")]
    [Description("Initialize a shared bare repository and its initial linked master worktree inside a workbench root.")]
    public CallToolResult VcInitShared(
        [Description("Root directory of the workbench.")] string workbenchRoot,
        [Description("Path for the initial master linked worktree. Must be inside workbenchRoot.")] string masterWorktreePath)
        => Invoke(() => RepositoryService.InitShared(workbenchRoot, masterWorktreePath));

    [McpServerTool(Name = "vc_add_worktree")]
    [Description("Create a branch and complete linked worktree backed by a shared bare repository.")]
    public CallToolResult VcAddWorktree(
        [Description("Path to the shared bare repository.")] string repositoryPath,
        [Description("Path for the new linked checkout. Must be inside the workbench root.")] string worktreePath,
        [Description("Name of the new branch.")] string branchName,
        [Description("Optional commit or branch from which to create the worktree.")] string? startPoint = null)
        => Invoke(() => RepositoryService.AddWorktree(
            repositoryPath,
            worktreePath,
            branchName,
            startPoint));

    [McpServerTool(Name = "vc_remove_worktree")]
    [Description("Remove a linked worktree from a shared bare repository (git worktree remove --force). DESTRUCTIVE: discards the checkout and any uncommitted changes in it. When deleteBranch is true, deletes only branchName if it is registered to this checkout; unrelated branches and master are protected.")]
    public CallToolResult VcRemoveWorktree(
        [Description("Path to the shared bare repository.")] string repositoryPath,
        [Description("Path of the linked checkout to remove. Must be inside the workbench root.")] string worktreePath,
        [Description("Branch to delete during rollback. It is deleted only when registered to worktreePath.")] string? branchName = null,
        [Description("Delete branchName only when it belongs to worktreePath. Defaults to false.")] bool deleteBranch = false)
        => Invoke(() => RepositoryService.RemoveWorktree(
            repositoryPath,
            worktreePath,
            branchName,
            deleteBranch));

    [McpServerTool(Name = "vc_worktrees")]
    [Description("List complete linked worktrees registered with a shared bare repository. Read-only.")]
    public CallToolResult VcWorktrees(
        [Description("Path to the shared bare repository.")] string repositoryPath)
        => Invoke(() => RepositoryService.Worktrees(repositoryPath));

    [McpServerTool(Name = "vc_merge")]
    [Description("Merge a source branch into a clean target linked worktree using a no-fast-forward merge.")]
    public CallToolResult VcMerge(
        [Description("Path to the target linked worktree.")] string targetWorktreePath,
        [Description("Source branch to merge.")] string sourceBranch)
        => Invoke(() => RepositoryService.Merge(targetWorktreePath, sourceBranch));

    [McpServerTool(Name = "vc_merge_validated")]
    [Description("Create a guarded no-fast-forward feature merge and immutable feature-merge evidence after the all-device validation gate.")]
    public CallToolResult VcMergeValidated(VcValidatedMergeRequest request) =>
        Invoke(() => RepositoryService.MergeValidated(request));

    [McpServerTool(Name = "vc_apply_historical_paths")]
    [Description("Write selected historical PLC source XML blobs into the current worktree without staging or committing.")]
    public CallToolResult VcApplyHistoricalPaths(string repoPath, string sourceSha, string[] paths) =>
        Invoke(() => RepositoryService.ApplyHistoricalPaths(repoPath, sourceSha, paths));

    [McpServerTool(Name = "vc_merge_preview")]
    [Description("Build a prospective merge tree for a source branch without changing branches, worktrees, or refs. Reports conflicts and fingerprints tracked PLC source XML in a conflict-free candidate tree.")]
    public CallToolResult VcMergePreview(
        [Description("Path to the current target repository or linked worktree.")] string repoPath,
        [Description("Source branch to preview merging into the current branch.")] string sourceBranch)
        => Invoke(() => RepositoryService.PreviewMerge(repoPath, sourceBranch));

    [McpServerTool(Name = "vc_status")]
    [Description("Show working-tree status: staged, unstaged, modified, added, deleted, and untracked files relative to HEAD.")]
    public CallToolResult VcStatus(
        [Description("Path to the git repository.")] string repoPath)
        => Invoke(() => RepositoryService.Status(repoPath));

    [McpServerTool(Name = "vc_add")]
    [Description("Compatibility tool that stages PLC source XML only. When paths is omitted or empty, stages every changed devices/<device>/source/**/*.xml file; other files are never staged.")]
    public CallToolResult VcAdd(
        [Description("Path to the git repository.")] string repoPath,
        [Description("File path(s) relative to repo root to stage. Omit or pass empty to stage all.")] string[]? paths = null)
        => Invoke(() => RepositoryService.Add(repoPath, paths));

    [McpServerTool(Name = "vc_commit")]
    [Description("Compatibility tool that commits already-staged PLC source XML only. Prefer vc_commit_selected for normal workbench commits.")]
    public CallToolResult VcCommit(
        [Description("Path to the git repository.")] string repoPath,
        [Description("Commit message.")] string message,
        [Description("Optional author string in 'Name <email>' format. Falls back to git config or 'PLC Assistant <assistant@plc-assistant.local>'.")] string? author = null)
        => Invoke(() => RepositoryService.Commit(repoPath, message, author));

    [McpServerTool(Name = "vc_commit_selected")]
    [Description("Atomically commit exactly the selected changed PLC source XML paths. Existing staging is cleared without changing working files; unselected changes remain uncommitted.")]
    public CallToolResult VcCommitSelected(
        [Description("Path to the git repository or linked worktree.")] string repoPath,
        [Description("One or more changed devices/<device>/source/**/*.xml paths relative to the worktree root.")] string[] paths,
        [Description("Required commit message.")] string message,
        [Description("Optional author string in 'Name <email>' format.")] string? author = null)
        => Invoke(() => RepositoryService.CommitSelected(repoPath, paths, message, author));

    [McpServerTool(Name = "vc_log")]
    [Description("Show commit history. Default last 20 commits; max 100. Optionally filter to commits touching a single file.")]
    public CallToolResult VcLog(
        [Description("Path to the git repository.")] string repoPath,
        [Description("Maximum number of commits (default 20, max 100).")] int? maxCount = null,
        [Description("Optional: filter log to commits touching this file path.")] string? filePath = null)
        => Invoke(() => RepositoryService.Log(repoPath, maxCount, filePath));

    [McpServerTool(Name = "vc_validation_get")]
    [Description("Read immutable TIA validation evidence for an exact commit. Returns null when the commit is unlabeled or has invalid evidence.")]
    public CallToolResult VcValidationGet(
        [Description("Path to the git repository.")] string repoPath,
        [Description("Full or resolvable commit SHA to inspect.")] string commitSha)
        => Invoke(() => RepositoryService.GetValidation(repoPath, commitSha)!);

    [McpServerTool(Name = "vc_validation_create")]
    [Description("Create immutable annotated TIA validation evidence for the current HEAD. App-internal use only.")]
    public CallToolResult VcValidationCreate(
        [Description("Path to the git repository.")] string repoPath,
        [Description("Validation evidence. Its commitSha must equal the current HEAD.")] VcValidationEvidence? evidence)
        => Invoke(() => RepositoryService.CreateValidation(repoPath, evidence));

    [McpServerTool(Name = "vc_diff")]
    [Description("Show an XML source diff and semantic summary. No refs compares HEAD to working tree; oldSha only compares that ref to working tree; both refs compare them; newSha only compares HEAD to that ref.")]
    public CallToolResult VcDiff(
        [Description("Path to the git repository.")] string repoPath,
        [Description("File path relative to repo root.")] string filePath,
        [Description("Optional old commit or ref. With no newSha, compares this ref to the working tree.")] string? oldSha = null,
        [Description("Optional new commit or ref. With no oldSha, compares HEAD to this ref.")] string? newSha = null)
        => Invoke(() => RepositoryService.Diff(repoPath, filePath, oldSha, newSha));

    [McpServerTool(Name = "vc_snapshot")]
    [Description("Compatibility checkpoint that stages and commits all changed PLC source XML only. Runtime, metadata, and other non-source files remain untouched.")]
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

    [McpServerTool(Name = "svn_init_shared")]
    [Description("Create the shared local SVN native store (repository.svn with native/main and native/branches) inside a workbench root. Returns the file:// repository URI.")]
    public CallToolResult SvnInitShared(
        [Description("Root directory of the workbench.")] string workbenchRoot)
        => Invoke(() => _svn.CreateShared(workbenchRoot));

    [McpServerTool(Name = "svn_checkout")]
    [Description("Check out a repository or branch URL into a local SVN working copy.")]
    public CallToolResult SvnCheckout(
        [Description("Repository file:// URI or branch URL, e.g. file:///.../repository.svn/native/main.")] string url,
        [Description("Local path for the working copy.")] string path)
        => Invoke(() => _svn.Checkout(url, path));

    [McpServerTool(Name = "svn_commit")]
    [Description("Recursively add all unversioned items below a working copy and commit it. Returns the committed revision.")]
    public CallToolResult SvnCommit(
        [Description("Path of the SVN working copy.")] string path,
        [Description("Commit message.")] string message)
        => Invoke(() =>
        {
            _svn.AddRecursive(path);
            return _svn.Commit(path, message);
        });

    [McpServerTool(Name = "svn_copy_branch")]
    [Description("Server-side copy of a branch at a peg revision into native/branches/<newBranch> of the same repository.")]
    public CallToolResult SvnCopyBranch(
        [Description("Repository file:// URI, e.g. file:///.../repository.svn.")] string repoUrl,
        [Description("Source branch under native/, e.g. 'main' or 'branches/feature-x'.")] string sourceBranch,
        [Description("Peg revision of the source branch to copy.")] long revision,
        [Description("Name of the new branch (single path segment).")] string newBranch,
        [Description("Commit message for the copy.")] string message)
        => Invoke(() => _svn.CopyBranch(
            $"{repoUrl.TrimEnd('/')}/native/{sourceBranch}",
            revision,
            newBranch,
            message));

    [McpServerTool(Name = "svn_status")]
    [Description("Show working-copy status: clean/dirty plus the changed entries. Read-only.")]
    public CallToolResult SvnStatus(
        [Description("Path of the SVN working copy.")] string path)
        => Invoke(() => _svn.Status(path));

    [McpServerTool(Name = "svn_log")]
    [Description("Show commit history of a working-copy path or repository URL, newest first. Default last 20 entries.")]
    public CallToolResult SvnLog(
        [Description("Working-copy path or repository URL.")] string path,
        [Description("Maximum number of log entries (default 20).")] int? limit = null)
        => Invoke(() => _svn.Log(path, limit ?? 20));

    [McpServerTool(Name = "svn_update")]
    [Description("Update a working copy to an exact revision. Used to pin a checked-out native project to the revision recorded in revision.json.")]
    public CallToolResult SvnUpdate(
        [Description("Path of the SVN working copy.")] string path,
        [Description("Target revision (zero or greater).")] long revision)
        => Invoke(() => _svn.UpdateToRevision(path, revision));

    private static readonly SvnRepositoryService _svn = new();

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
