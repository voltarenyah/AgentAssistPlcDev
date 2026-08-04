using System.Text;
using System.Text.RegularExpressions;
using LibGit2Sharp;

namespace Mcp.VersionControl.Git;

/// <summary>
/// Static methods wrapping LibGit2Sharp repository operations.
/// Every method opens the repo from path and closes on return (safe for stateless MCP calls).
/// </summary>
internal static class RepositoryService
{
    private static readonly Signature DefaultAuthor = new("PLC Assistant", "assistant@plc-assistant.local", DateTimeOffset.UtcNow);
    private static readonly GitCommandRunner Git = new(TimeSpan.FromSeconds(30));

    /// <summary>Default .gitignore for PLC workbench export roots.</summary>
    private const string DefaultGitIgnore = """
        # PLC Assistant — version-controlled export root
        # Project-level configuration and user settings
        *.user
        *.userosettings
        *.userprefs
        # SQLite knowledge bases (rebuilt on ingest)
        *.db
        *.db-journal
        *.db-wal
        *.db-shm
        # OS / editor artifacts
        Thumbs.db
        .DS_Store
        Desktop.ini
        # Build output (when project roots happen to overlap build dirs)
        bin/
        obj/
        # NuGet packages
        packages/
        """;

    private static readonly string[] SharedExcludeRules =
    {
        "worktree.json",
        "devices/*/device.json",
        "devices/*/staging/",
        "devices/*/plc-knowledge.db*",
        ".automation/",
        "sessionexport/",
    };

    /// <summary>Regex to parse unified-diff hunk headers: @@ -oldStart,oldCount +newStart,newCount @@</summary>
    private static readonly Regex HunkHeaderRegex = new(
        @"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@",
        RegexOptions.Compiled);

    /// <summary>Init a git repo at repoPath. Idempotent: no-op if .git already exists.</summary>
    public static VcInitResult Init(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new VcInternalException("PATH_REQUIRED", "repoPath must not be empty.");

        var gitDir = Path.Combine(repoPath, ".git");
        if (Directory.Exists(gitDir))
        {
            return new VcInitResult { RepoPath = repoPath, Initialized = true, ExistingRepo = true };
        }

        Repository.Init(repoPath);
        WriteGitIgnore(repoPath);

        return new VcInitResult { RepoPath = repoPath, Initialized = true, ExistingRepo = false };
    }

    /// <summary>
    /// Initialize shared bare storage and its initial linked master worktree.
    /// Both storage and checkout remain contained by the workbench root.
    /// </summary>
    public static VcSharedInitResult InitShared(string workbenchRoot, string masterWorktreePath)
    {
        var root = RequireFullPath(workbenchRoot, nameof(workbenchRoot));
        var masterPath = RequireFullPath(masterWorktreePath, nameof(masterWorktreePath));
        EnsureContained(root, masterPath);
        EnsureNoReparsePoints(root);
        EnsureNoReparsePoints(masterPath);

        var repositoryPath = Path.Combine(root, "repository.git");
        EnsureNoReparsePoints(repositoryPath);
        var masterGitFile = Path.Combine(masterPath, ".git");
        var existingMaster = File.Exists(masterGitFile);

        if (existingMaster)
        {
            if (!Repository.IsValid(masterPath))
            {
                throw new VcInternalException(
                    "WORKTREE_EXISTS",
                    $"'{masterPath}' exists but is not a valid linked Git worktree.");
            }

            var actualRepositoryPath = ResolveCommonRepositoryPath(masterPath);
            if (!PathsEqual(repositoryPath, actualRepositoryPath))
            {
                throw new VcInternalException(
                    "WORKTREE_REPOSITORY_MISMATCH",
                    $"Master worktree '{masterPath}' is linked to '{actualRepositoryPath}', not expected repository '{repositoryPath}'.");
            }
        }

        var existingRepository = Repository.IsValid(repositoryPath);

        if (Directory.Exists(repositoryPath) && !existingRepository)
        {
            throw new VcInternalException(
                "REPOSITORY_EXISTS",
                $"'{repositoryPath}' already exists but is not a valid Git repository.");
        }

        Directory.CreateDirectory(root);
        if (!existingRepository)
        {
            RunGit(
                "GIT_INIT_FAILED",
                "Failed to initialize the shared bare repository.",
                "init", "--bare", repositoryPath);
        }

        if (!existingMaster)
        {
            if (Directory.Exists(masterPath) || File.Exists(masterPath))
            {
                throw new VcInternalException(
                    "WORKTREE_EXISTS",
                    $"The master worktree path '{masterPath}' already exists.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(masterPath)!);
            RunGit(
                "WORKTREE_ADD_FAILED",
                "Failed to create the initial master worktree.",
                "--git-dir", repositoryPath,
                "worktree", "add", "--orphan", "-b", "master", masterPath);
        }

        WriteSharedExclude(repositoryPath);
        return new VcSharedInitResult
        {
            WorkbenchRoot = root,
            RepositoryPath = repositoryPath,
            MasterWorktreePath = masterPath,
            ExistingRepository = existingRepository,
        };
    }

    /// <summary>Create a branch and complete linked checkout backed by a shared bare repository.</summary>
    public static VcWorktreeResult AddWorktree(
        string repositoryPath,
        string worktreePath,
        string branchName,
        string? startPoint)
    {
        var repository = RequireFullPath(repositoryPath, nameof(repositoryPath));
        EnsureNoReparsePoints(repository);
        EnsureRepo(repository);
        if (string.IsNullOrWhiteSpace(branchName))
        {
            throw new VcInternalException("BRANCH_REQUIRED", "branchName must not be empty.");
        }

        var workbenchRoot = Directory.GetParent(repository)?.FullName
            ?? throw new VcInternalException(
                "INVALID_REPOSITORY_PATH",
                $"The repository path '{repository}' has no containing workbench directory.");
        var checkout = RequireFullPath(worktreePath, nameof(worktreePath));
        EnsureContained(workbenchRoot, checkout);
        EnsureNoReparsePoints(checkout);

        if (Directory.Exists(checkout) || File.Exists(checkout))
        {
            throw new VcInternalException(
                "WORKTREE_EXISTS",
                $"The worktree path '{checkout}' already exists.");
        }

        using (var repo = new Repository(repository))
        {
            if (repo.Branches[branchName] != null)
            {
                throw new VcInternalException(
                    "BRANCH_EXISTS",
                    $"Branch '{branchName}' already exists.");
            }
        }

        var arguments = new List<string>
        {
            "--git-dir", repository,
            "worktree", "add", "-b", branchName, checkout,
        };
        if (!string.IsNullOrWhiteSpace(startPoint))
        {
            arguments.Add(startPoint);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(checkout)!);
        RunGit(
            "WORKTREE_ADD_FAILED",
            $"Failed to create linked worktree '{branchName}'.",
            arguments.ToArray());

        using var linkedRepository = new Repository(checkout);
        return new VcWorktreeResult
        {
            RepositoryPath = repository,
            WorktreePath = checkout,
            Branch = linkedRepository.Head.FriendlyName,
            Sha = linkedRepository.Head.Tip?.Sha ?? string.Empty,
        };
    }

    /// <summary>Remove a linked worktree from a shared bare repository (git worktree remove --force).</summary>
    public static VcWorktreeRemoveResult RemoveWorktree(string repositoryPath, string worktreePath)
    {
        var repository = RequireFullPath(repositoryPath, nameof(repositoryPath));
        EnsureNoReparsePoints(repository);
        EnsureRepo(repository);
        var workbenchRoot = Directory.GetParent(repository)?.FullName
            ?? throw new VcInternalException(
                "INVALID_REPOSITORY_PATH",
                $"The repository path '{repository}' has no containing workbench directory.");
        var checkout = RequireFullPath(worktreePath, nameof(worktreePath));
        EnsureContained(workbenchRoot, checkout);

        RunGit(
            "WORKTREE_REMOVE_FAILED",
            $"Failed to remove linked worktree '{checkout}'.",
            "--git-dir", repository,
            "worktree", "remove", "--force", checkout);

        return new VcWorktreeRemoveResult
        {
            RepositoryPath = repository,
            WorktreePath = checkout,
            Removed = true,
        };
    }

    /// <summary>List complete linked checkouts registered with a shared bare repository.</summary>
    public static VcWorktreeListResult Worktrees(string repositoryPath)
    {
        var repository = RequireFullPath(repositoryPath, nameof(repositoryPath));
        EnsureNoReparsePoints(repository);
        EnsureRepo(repository);
        var output = RunGit(
            "WORKTREE_LIST_FAILED",
            "Failed to list linked worktrees.",
            "--git-dir", repository,
            "worktree", "list", "--porcelain");

        return new VcWorktreeListResult
        {
            RepositoryPath = repository,
            Worktrees = ParseWorktrees(output).ToArray(),
        };
    }

    /// <summary>Merge a shared branch into a clean linked target worktree.</summary>
    public static VcMergeResult Merge(string targetWorktreePath, string sourceBranch)
    {
        if (string.IsNullOrWhiteSpace(sourceBranch))
        {
            throw new VcInternalException("BRANCH_REQUIRED", "sourceBranch must not be empty.");
        }

        var target = RequireFullPath(targetWorktreePath, nameof(targetWorktreePath));
        EnsureRepo(target);
        using (var targetRepository = new Repository(target))
        {
            if (targetRepository.RetrieveStatus(new StatusOptions
                {
                    IncludeUntracked = true,
                    RecurseUntrackedDirs = true,
                }).Any(entry => !entry.State.HasFlag(FileStatus.Ignored)))
            {
                throw new VcInternalException(
                    "DIRTY_WORKTREE",
                    $"Target worktree '{target}' has uncommitted changes.",
                    "Commit or restore target changes before merging.");
            }

            if (targetRepository.Branches[sourceBranch]?.Tip == null)
            {
                throw new VcInternalException(
                    "BRANCH_NOT_FOUND",
                    $"Branch '{sourceBranch}' was not found.");
            }
        }

        string sourceSha;
        using (var repository = new Repository(target))
        {
            sourceSha = repository.Branches[sourceBranch]!.Tip!.Sha;
        }

        RunGit(
            "MERGE_FAILED",
            $"Failed to merge branch '{sourceBranch}' into '{target}'.",
            "-C", target,
            "merge", "--no-ff", sourceBranch);

        using var mergedRepository = new Repository(target);
        return new VcMergeResult
        {
            TargetWorktreePath = target,
            TargetBranch = mergedRepository.Head.FriendlyName,
            SourceBranch = sourceBranch,
            SourceSha = sourceSha,
            Sha = mergedRepository.Head.Tip?.Sha ?? string.Empty,
            Merged = true,
        };
    }

    /// <summary>Show working-tree status.</summary>
    public static VcStatusResult Status(string repoPath)
    {
        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
            IncludeIgnored = false,
        });

        var branch = repo.Head?.FriendlyName ?? "HEAD";
        var entries = new List<VcStatusEntry>();

        foreach (var entry in status)
        {
            var filePath = entry.FilePath.Replace('\\', '/');
            if (entry.State.HasFlag(FileStatus.Ignored) ||
                !SourcePathPolicy.IsAllowed(filePath))
            {
                continue;
            }

            entries.Add(new VcStatusEntry
            {
                FilePath = filePath,
                State = MapFileStatus(entry.State),
                Staged = IsStaged(entry.State),
            });
        }

        return new VcStatusResult
        {
            RepoPath = repoPath,
            Branch = branch,
            Entries = entries.ToArray(),
        };
    }

    /// <summary>Stage allowed PLC source XML. Null or empty paths stage every allowed change.</summary>
    public static VcAddResult Add(string repoPath, string[]? paths = null)
    {
        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);

        string[] pathsToStage;
        if (paths is { Length: > 0 })
        {
            // Validate the complete request before touching the index so a mixed
            // allowed/forbidden request cannot partially stage files.
            pathsToStage = paths
                .Select(SourcePathPolicy.Require)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            pathsToStage = EnumerateAllowedChangedPaths(repo);
        }

        if (pathsToStage.Length > 0)
        {
            Commands.Stage(repo, pathsToStage);
        }

        return new VcAddResult
        {
            Staged = EnumerateStagedPaths(repo).Count(SourcePathPolicy.IsAllowed),
        };
    }

    /// <summary>Commit staged changes. Returns the commit SHA.</summary>
    public static VcCommitResult Commit(string repoPath, string message, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new VcInternalException("MESSAGE_REQUIRED", "Commit message must not be empty.");

        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);
        EnsureOnlyAllowedPathsAreStaged(repo);
        var signature = ResolveAuthor(repo, author);
        var commit = repo.Commit(message, signature, signature);
        return new VcCommitResult { Sha = commit.Sha, Message = message };
    }

    /// <summary>Show commit history. Capped at maxCount (default 20, max 100). Optionally filter to one file.</summary>
    public static VcLogResult Log(string repoPath, int? maxCount = null, string? filePath = null)
    {
        var cap = Math.Clamp(maxCount ?? 20, 1, 100);
        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);

        IEnumerable<Commit> commits;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var filter = new CommitFilter { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time };
            // Git tree paths are repository-relative and always use '/'. Keeping the
            // caller's path in that form is required by LibGit2Sharp's QueryBy on
            // Windows linked worktrees; converting to '\\' silently returns no commits.
            commits = repo.Commits.QueryBy(filePath.Replace('\\', '/'), filter).Take(cap).Select(c => c.Commit);
        }
        else
        {
            commits = repo.Commits.Take(cap);
        }

        var entries = commits.Select(c => new VcCommitEntry
        {
            Sha = c.Sha,
            Author = c.Author?.Name ?? "unknown",
            Message = c.MessageShort ?? c.Message ?? "",
            Timestamp = c.Author?.When.UtcDateTime.ToString("O") ?? "",
            Files = c.Tree?.Select(e => e.Name).ToArray() ?? Array.Empty<string>(),
        }).ToArray();

        return new VcLogResult { RepoPath = repoPath, Commits = entries };
    }

    /// <summary>
    /// Show diff for a single file against HEAD, or between two arbitrary refs.
    /// Returns structured hunks parsed from the unified-diff output.
    /// </summary>
    public static VcDiffResult Diff(string repoPath, string filePath, string? oldSha = null, string? newSha = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new VcInternalException("FILE_REQUIRED", "filePath must not be empty.");

        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);

        var normalizedPath = filePath.Replace('\\', '/');

        // Determine old and new trees
        Tree? oldTree = null;
        Tree? newTree = null;
        string? resolvedOldSha = oldSha;
        string? resolvedNewSha = newSha;

        if (!string.IsNullOrWhiteSpace(oldSha))
        {
            var oldCommit = repo.Lookup<Commit>(oldSha);
            if (oldCommit != null) oldTree = oldCommit.Tree;
        }

        if (!string.IsNullOrWhiteSpace(newSha))
        {
            var newCommit = repo.Lookup<Commit>(newSha);
            if (newCommit != null) newTree = newCommit.Tree;
        }
        else if (oldTree != null)
        {
            // oldSha provided but no newSha → compare against HEAD
            newTree = repo.Head?.Tip?.Tree;
            resolvedNewSha = repo.Head?.Tip?.Sha;
        }
        else
        {
            // No refs → working tree vs HEAD
            oldTree = repo.Head?.Tip?.Tree;
            resolvedOldSha = repo.Head?.Tip?.Sha;
        }

        // Generate the patch and parse it into structured hunks
        var patch = repo.Diff.Compare<Patch>(oldTree, newTree);
        var entry = patch.FirstOrDefault(e =>
            e.Path.Replace('\\', '/').Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            return new VcDiffResult
            {
                RepoPath = repoPath,
                FilePath = filePath,
                OldSha = resolvedOldSha,
                NewSha = resolvedNewSha,
                Binary = false,
                Hunks = Array.Empty<VcDiffHunk>(),
            };
        }

        return new VcDiffResult
        {
            RepoPath = repoPath,
            FilePath = filePath,
            OldSha = resolvedOldSha,
            NewSha = resolvedNewSha,
            Binary = entry.IsBinaryComparison,
            Hunks = ParseUnifiedDiff(entry.Patch).ToArray(),
        };
    }

    /// <summary>Stage all allowed PLC source XML changes and commit them.</summary>
    public static VcCommitResult Snapshot(string repoPath, string? message = null)
    {
        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);

        var pathsToStage = EnumerateAllowedChangedPaths(repo);
        if (pathsToStage.Length > 0)
        {
            Commands.Stage(repo, pathsToStage);
        }
        EnsureOnlyAllowedPathsAreStaged(repo);

        var msg = message ?? $"checkpoint before operation — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC";
        var signature = ResolveAuthor(repo, null);
        var commit = repo.Commit(msg, signature, signature);
        return new VcCommitResult { Sha = commit.Sha, Message = msg };
    }

    /// <summary>
    /// Restore file(s) from a given commit. Destructive: overwrites working tree.
    /// Uses CheckoutPaths for file-level restore.
    /// </summary>
    public static VcRestoreResult Restore(string repoPath, string? filePath = null, string? sourceSha = null)
    {
        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);

        string refSha;
        if (!string.IsNullOrWhiteSpace(sourceSha))
        {
            var commit = repo.Lookup<Commit>(sourceSha);
            if (commit == null)
                throw new VcInternalException("COMMIT_NOT_FOUND", $"Commit '{sourceSha}' was not found.");
            refSha = commit.Sha;
        }
        else
        {
            if (repo.Head?.Tip == null)
                throw new VcInternalException("NO_COMMITS", "Repository has no commits yet.");
            refSha = repo.Head.Tip.Sha;
        }

        var restored = new List<string>();
        var opts = new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force };

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var normalized = filePath.Replace('/', '\\');
            repo.CheckoutPaths(refSha, new[] { normalized }, opts);
            restored.Add(filePath);
        }
        else
        {
            var tip = repo.Head?.Tip;
            if (tip?.Tree != null)
            {
                var paths = tip.Tree.Select(e => e.Path).ToArray();
                repo.CheckoutPaths(refSha, paths, opts);
                restored.AddRange(paths.Select(p => p.Replace('\\', '/')));
            }
        }

        return new VcRestoreResult { Restored = restored.ToArray() };
    }

    /// <summary>List local branches with current (HEAD) marker.</summary>
    public static VcBranchesResult Branches(string repoPath)
    {
        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);

        var branches = repo.Branches
            .Where(b => !b.IsRemote)
            .Select(b => new VcBranchInfo
            {
                Name = b.FriendlyName,
                IsHead = b.IsCurrentRepositoryHead,
                Sha = b.Tip?.Sha ?? "",
                Upstream = b.IsTracking ? b.TrackedBranch?.FriendlyName : null,
            })
            .ToArray();

        return new VcBranchesResult { Branches = branches };
    }

    /// <summary>Switch to a branch (git checkout). Fails if there are uncommitted changes that would be lost.</summary>
    public static VcCheckoutResult Checkout(string repoPath, string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
            throw new VcInternalException("BRANCH_REQUIRED", "branchName must not be empty.");

        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);

        var branch = repo.Branches[branchName];
        if (branch == null)
            throw new VcInternalException("BRANCH_NOT_FOUND",
                $"Branch '{branchName}' was not found.",
                "Use vc_branches to list available branches.");

        LibGit2Sharp.Commands.Checkout(repo, branch);
        return new VcCheckoutResult
        {
            Branch = branch.FriendlyName,
            Sha = branch.Tip?.Sha ?? "",
        };
    }

    /// <summary>Get or set a git config entry (local level).</summary>
    public static VcConfigResult Config(string repoPath, string key, string? value = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new VcInternalException("KEY_REQUIRED", "Config key must not be empty.");

        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);

        if (value != null)
        {
            try
            {
                repo.Config.Set(key, value, ConfigurationLevel.Local);
            }
            catch (LibGit2SharpException)
            {
                // Config system not available on this machine — skip silently
            }
            return new VcConfigResult { Key = key, Value = value, Operation = "set" };
        }
        else
        {
            string? currentValue = null;
            try
            {
                currentValue = repo.Config.Get<string>(key)?.Value;
            }
            catch (LibGit2SharpException)
            {
                // Config not available on this machine
            }
            return new VcConfigResult
            {
                Key = key,
                Value = currentValue,
                Operation = "read",
            };
        }
    }

    /* ── Private helpers ──────────────────────────────────── */

    private static void EnsureRepo(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new VcInternalException("PATH_REQUIRED", "repoPath must not be empty.");

        if (!Repository.IsValid(repoPath))
            throw new VcInternalException("NOT_A_REPO",
                $"'{repoPath}' is not a git repository (no .git directory found).",
                "Run vc_init first to initialize a repository.");
    }

    private static void WriteGitIgnore(string repoPath)
    {
        var gitIgnorePath = Path.Combine(repoPath, ".gitignore");
        if (!File.Exists(gitIgnorePath))
        {
            File.WriteAllText(gitIgnorePath, DefaultGitIgnore, Encoding.UTF8);
        }
    }

    private static void WriteSharedExclude(string repositoryPath)
    {
        var infoDirectory = Path.Combine(repositoryPath, "info");
        Directory.CreateDirectory(infoDirectory);
        var excludePath = Path.Combine(infoDirectory, "exclude");
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (!File.Exists(excludePath))
        {
            File.WriteAllText(excludePath, string.Empty, encoding);
        }

        var content = File.ReadAllText(excludePath);
        var existingRules = content
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .ToHashSet(StringComparer.Ordinal);
        var missingRules = SharedExcludeRules
            .Where(rule => !existingRules.Contains(rule))
            .ToArray();
        if (missingRules.Length == 0)
        {
            return;
        }

        var append = new StringBuilder();
        if (content.Length > 0 &&
            !content.EndsWith("\r", StringComparison.Ordinal) &&
            !content.EndsWith("\n", StringComparison.Ordinal))
        {
            append.Append(Environment.NewLine);
        }
        foreach (var rule in missingRules)
        {
            append.Append(rule);
            append.Append(Environment.NewLine);
        }

        File.AppendAllText(excludePath, append.ToString(), encoding);
    }

    private static string[] EnumerateAllowedChangedPaths(Repository repo) =>
        RetrieveCompleteStatus(repo)
            .Where(entry => !entry.State.HasFlag(FileStatus.Ignored))
            .Select(entry => entry.FilePath.Replace('\\', '/'))
            .Where(SourcePathPolicy.IsAllowed)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] EnumerateStagedPaths(Repository repo) =>
        RetrieveCompleteStatus(repo)
            .Where(entry => IsStaged(entry.State))
            .Select(entry => entry.FilePath.Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void EnsureOnlyAllowedPathsAreStaged(Repository repo)
    {
        var forbiddenPaths = EnumerateStagedPaths(repo)
            .Where(path => !SourcePathPolicy.IsAllowed(path))
            .ToArray();
        if (forbiddenPaths.Length == 0)
        {
            return;
        }

        throw new VcInternalException(
            "SOURCE_PATH_REQUIRED",
            $"The Git index contains path(s) outside tracked PLC source XML: {string.Join(", ", forbiddenPaths)}.",
            "Unstage non-source files before committing.");
    }

    private static RepositoryStatus RetrieveCompleteStatus(Repository repo) =>
        repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
            IncludeIgnored = false,
        });

    private static string RequireFullPath(string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new VcInternalException(
                "PATH_REQUIRED",
                $"{parameterName} must not be empty.");
        }

        return Path.GetFullPath(path).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static void EnsureContained(string rootPath, string candidatePath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = rootPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!candidatePath.StartsWith(prefix, comparison))
        {
            throw new VcInternalException(
                "PATH_OUTSIDE_WORKBENCH",
                $"Path '{candidatePath}' must remain under workbench root '{rootPath}'.");
        }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(pathRoot))
        {
            throw new VcInternalException(
                "INVALID_PATH",
                $"Path '{path}' has no filesystem root.");
        }

        var current = pathRoot;
        foreach (var segment in fullPath[pathRoot.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new VcInternalException(
                    "REPARSE_POINT_NOT_ALLOWED",
                    $"Path '{path}' traverses reparse point '{current}'.");
            }
        }
    }

    private static string ResolveCommonRepositoryPath(string worktreePath)
    {
        var output = RunGit(
            "WORKTREE_VALIDATION_FAILED",
            $"Failed to verify linked worktree '{worktreePath}'.",
            "-C", worktreePath,
            "rev-parse", "--path-format=absolute", "--git-common-dir").Trim();
        return RequireFullPath(output, "gitCommonDirectory");
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            RequireFullPath(left, nameof(left)),
            RequireFullPath(right, nameof(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static IEnumerable<VcWorktreeInfo> ParseWorktrees(string output)
    {
        foreach (var block in Regex.Split(output.Trim(), @"\r?\n\r?\n"))
        {
            if (string.IsNullOrWhiteSpace(block))
            {
                continue;
            }

            string? path = null;
            var sha = string.Empty;
            var branch = string.Empty;
            var detached = false;
            var locked = false;
            var bare = false;

            foreach (var line in block.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (line.StartsWith("worktree ", StringComparison.Ordinal))
                    path = line["worktree ".Length..];
                else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
                    sha = line["HEAD ".Length..];
                else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
                    branch = line["branch refs/heads/".Length..];
                else if (line.Equals("detached", StringComparison.Ordinal))
                    detached = true;
                else if (line.StartsWith("locked", StringComparison.Ordinal))
                    locked = true;
                else if (line.Equals("bare", StringComparison.Ordinal))
                    bare = true;
            }

            if (bare || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            yield return new VcWorktreeInfo
            {
                WorktreePath = Path.GetFullPath(path),
                Branch = branch,
                Sha = sha,
                Detached = detached,
                Locked = locked,
            };
        }
    }

    private static string RunGit(string errorCode, string errorMessage, params string[] arguments)
        => Git.Run("git", errorCode, errorMessage, arguments);

    private static Signature ResolveAuthor(Repository repo, string? author)
    {
        if (!string.IsNullOrWhiteSpace(author))
        {
            var match = Regex.Match(author, @"^(.+?)\s*<(.+?)>\s*$");
            if (match.Success)
            {
                return new Signature(match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim(), DateTimeOffset.UtcNow);
            }
            return new Signature(author, "assistant@plc-assistant.local", DateTimeOffset.UtcNow);
        }

        try
        {
            var name = repo.Config.Get<string>("user.name")?.Value;
            var email = repo.Config.Get<string>("user.email")?.Value;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(email))
            {
                return new Signature(name, email, DateTimeOffset.UtcNow);
            }
        }
        catch (LibGit2SharpException)
        {
            // Config not available on this machine — use default
        }

        return DefaultAuthor;
    }

    private static string MapFileStatus(FileStatus status)
    {
        if (status.HasFlag(FileStatus.NewInWorkdir)) return "Untracked";
        if (status.HasFlag(FileStatus.ModifiedInWorkdir)) return "Modified";
        if (status.HasFlag(FileStatus.DeletedFromWorkdir)) return "Deleted";
        if (status.HasFlag(FileStatus.NewInIndex)) return "Added";
        if (status.HasFlag(FileStatus.ModifiedInIndex)) return "Staged";
        if (status.HasFlag(FileStatus.DeletedFromIndex)) return "Deleted";
        if (status.HasFlag(FileStatus.RenamedInWorkdir)) return "RenamedInWorkdir";
        if (status.HasFlag(FileStatus.RenamedInIndex)) return "RenamedInIndex";
        if (status.HasFlag(FileStatus.TypeChangeInWorkdir)) return "Modified";
        if (status.HasFlag(FileStatus.TypeChangeInIndex)) return "Staged";
        if (status.HasFlag(FileStatus.Conflicted)) return "Conflicted";
        if (status.HasFlag(FileStatus.Ignored)) return "Ignored";
        return "Modified";
    }

    private static bool IsStaged(FileStatus status)
    {
        return status.HasFlag(FileStatus.NewInIndex)
            || status.HasFlag(FileStatus.ModifiedInIndex)
            || status.HasFlag(FileStatus.DeletedFromIndex)
            || status.HasFlag(FileStatus.RenamedInIndex)
            || status.HasFlag(FileStatus.TypeChangeInIndex);
    }

    /// <summary>Parse unified-diff text into structured hunks.</summary>
    private static List<VcDiffHunk> ParseUnifiedDiff(string diffText)
    {
        if (string.IsNullOrEmpty(diffText))
            return new List<VcDiffHunk>();

        var hunks = new List<VcDiffHunk>();
        var lines = diffText.Split('\n');
        List<VcDiffLine>? currentLines = null;
        int oldStart = 0, newStart = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            var match = HunkHeaderRegex.Match(line);
            if (match.Success)
            {
                // Save previous hunk
                if (currentLines != null)
                {
                    hunks.Add(new VcDiffHunk { OldStart = oldStart, NewStart = newStart, Lines = currentLines.ToArray() });
                }

                oldStart = int.Parse(match.Groups[1].Value);
                newStart = int.Parse(match.Groups[3].Value);
                currentLines = new List<VcDiffLine>();
                continue;
            }

            if (currentLines == null) continue;

            if (line.StartsWith("+"))
            {
                currentLines.Add(new VcDiffLine { Type = "addition", Content = line.Substring(1) });
            }
            else if (line.StartsWith("-"))
            {
                currentLines.Add(new VcDiffLine { Type = "deletion", Content = line.Substring(1) });
            }
            else if (line.StartsWith("\\")) // No newline at end of file
            {
                // Skip — cosmetic diff metadata
                continue;
            }
            else
            {
                // Context line (starts with space)
                var content = line.Length > 0 && line[0] == ' ' ? line.Substring(1) : line;
                currentLines.Add(new VcDiffLine { Type = "context", Content = content });
            }
        }

        // Last hunk
        if (currentLines != null)
        {
            hunks.Add(new VcDiffHunk { OldStart = oldStart, NewStart = newStart, Lines = currentLines.ToArray() });
        }

        return hunks;
    }
}

/// <summary>Internal exception for the git service layer (mapped to VcException by tools).</summary>
internal sealed class VcInternalException : Exception
{
    public VcInternalException(string code, string message, string? remediation = null) : base(message)
    {
        Code = code;
        Remediation = remediation ?? string.Empty;
    }

    public string Code { get; }
    public string Remediation { get; }
}
