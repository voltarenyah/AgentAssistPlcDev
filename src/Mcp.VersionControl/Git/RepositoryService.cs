using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Contracts.Engineering;
using LibGit2Sharp;

namespace Mcp.VersionControl.Git;

/// <summary>
/// Static methods wrapping LibGit2Sharp repository operations.
/// Every method opens the repo from path and closes on return (safe for stateless MCP calls).
/// </summary>
internal static class RepositoryService
{
    private static Signature DefaultAuthor => new("PLC Assistant", "assistant@plc-assistant.local", DateTimeOffset.UtcNow);
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
        "devices/*/source/metadata.json",
        "devices/*/plc-knowledge.db*",
        ".automation/",
        "sessionexport/",
        "repository.svn/",
        "tia/",
    };

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
            // PLC group nesting plus long block names can exceed the Windows 260-char path
            // limit inside worktrees; allow git to handle long paths on Windows.
            RunGit(
                "GIT_CONFIG_FAILED",
                "Failed to enable long path support on the shared repository.",
                "-C", repositoryPath, "config", "core.longpaths", "true");
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

    /// <summary>
    /// Remove a linked worktree from a shared bare repository. When deleteBranch is true,
    /// the branch is deleted only when it is the branch registered to worktreePath; a missing
    /// or unrelated branch is never deleted.
    /// </summary>
    public static VcWorktreeRemoveResult RemoveWorktree(
        string repositoryPath,
        string worktreePath,
        string? branchName = null,
        bool deleteBranch = false)
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

        if (deleteBranch && string.IsNullOrWhiteSpace(branchName))
        {
            throw new VcInternalException(
                "BRANCH_REQUIRED",
                "branchName is required when deleteBranch is true.");
        }

        var registered = Worktrees(repository).Worktrees.SingleOrDefault(item =>
            string.Equals(
                RequireFullPath(item.WorktreePath, nameof(item.WorktreePath)),
                checkout,
                StringComparison.OrdinalIgnoreCase));
        if (deleteBranch
            && (string.Equals(branchName, "master", StringComparison.OrdinalIgnoreCase)
                || string.Equals(registered?.Branch, "master", StringComparison.OrdinalIgnoreCase)))
        {
            throw new VcInternalException(
                "MASTER_WORKTREE_PROTECTED",
                "The master worktree and branch cannot be removed during rollback.");
        }
        var deleteRegisteredBranch = deleteBranch
            && registered is not null
            && string.Equals(registered.Branch, branchName, StringComparison.Ordinal)
            && !string.Equals(registered.Branch, "master", StringComparison.OrdinalIgnoreCase);

        // A rollback may run after vc_add_worktree failed before Git registered the checkout.
        // Do not infer ownership of a branch from its name alone.
        if (deleteBranch && registered is null)
        {
            return new VcWorktreeRemoveResult
            {
                RepositoryPath = repository,
                WorktreePath = checkout,
                Removed = false,
                BranchDeleted = false,
            };
        }

        RunGit(
            "WORKTREE_REMOVE_FAILED",
            $"Failed to remove linked worktree '{checkout}'.",
            "--git-dir", repository,
            "worktree", "remove", "--force", checkout);

        if (deleteRegisteredBranch)
        {
            try
            {
                using var linkedRepository = new Repository(repository);
                linkedRepository.Branches.Remove(branchName!);
            }
            catch (Exception exception) when (exception is LibGit2SharpException or ArgumentException)
            {
                throw new VcInternalException(
                    "BRANCH_DELETE_FAILED",
                    $"Failed to delete newly created branch '{branchName}'.",
                    exception.Message);
            }
        }

        return new VcWorktreeRemoveResult
        {
            RepositoryPath = repository,
            WorktreePath = checkout,
            Removed = true,
            BranchDeleted = deleteRegisteredBranch,
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
            var targetStatus = targetRepository.RetrieveStatus(new StatusOptions
                {
                    IncludeUntracked = true,
                    RecurseUntrackedDirs = true,
                })
                .Where(entry => !entry.State.HasFlag(FileStatus.Ignored))
                .ToArray();
            if (targetStatus.Length > 0)
            {
                throw new VcInternalException(
                    "DIRTY_WORKTREE",
                    $"Target worktree '{target}' has uncommitted changes: {string.Join(", ", targetStatus.Select(entry => $"{entry.FilePath}:{entry.State}"))}.",
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

    /// <summary>Preview a branch merge without changing the worktree or any Git ref.</summary>
    public static VcMergePreviewResult PreviewMerge(string repoPath, string sourceBranch)
    {
        EnsureRepo(repoPath);
        return MergePreviewService.Preview(repoPath, sourceBranch);
    }

    /// <summary>Guarded no-fast-forward merge that publishes immutable feature-merge evidence.</summary>
    public static VcValidatedMergeResult MergeValidated(VcValidatedMergeRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var target = RequireFullPath(request.TargetWorktreePath, nameof(request.TargetWorktreePath));
        EnsureRepo(target);
        using var repository = new Repository(target);
        var currentTarget = repository.Head?.Tip?.Sha
            ?? throw new VcInternalException("HEAD_REQUIRED", "Validated merge requires a target HEAD.");
        var source = repository.Branches[request.SourceBranch]?.Tip
            ?? throw new VcInternalException("BRANCH_NOT_FOUND", $"Branch '{request.SourceBranch}' was not found.");
        if (!string.Equals(currentTarget, request.ExpectedTargetSha, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(source.Sha, request.ExpectedSourceSha, StringComparison.OrdinalIgnoreCase))
            throw new VcInternalException("BRANCH_MOVED", "The target or source branch moved after validation.");
        if (repository.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true }).Any(entry => !entry.State.HasFlag(FileStatus.Ignored)))
            throw new VcInternalException("DIRTY_WORKTREE", "The target worktree must be clean before a validated merge.");

        var preview = MergePreviewService.Preview(target, request.SourceBranch);
        if (preview.HasConflicts || !string.Equals(preview.CandidateTreeSha, request.CandidateTreeSha, StringComparison.OrdinalIgnoreCase))
            throw new VcInternalException("CANDIDATE_TREE_CHANGED", "The prospective merge tree changed after validation.");

        string mergedSha = string.Empty;
        try
        {
            RunGit("VALIDATED_MERGE_FAILED", "The validated merge could not be created.", "-C", target, "merge", "--no-ff", "--no-edit", request.SourceBranch);
            using var merged = new Repository(target);
            var commit = merged.Head?.Tip ?? throw new VcInternalException("MERGE_FAILED", "Validated merge produced no commit.");
            if (commit.Parents.Count() != 2 || !string.Equals(commit.Tree.Sha, request.CandidateTreeSha, StringComparison.OrdinalIgnoreCase))
                throw new VcInternalException("MERGE_TREE_MISMATCH", "The created merge commit does not match the validated prospective tree.");
            mergedSha = commit.Sha;
            var evidence = request.Evidence with { CommitSha = mergedSha, EvidenceKind = "feature-merge" };
            var normalized = ValidationTagStore.Create(merged, evidence);
            return new VcValidatedMergeResult(true, mergedSha, normalized, ValidationTagStore.TagName(mergedSha));
        }
        catch (Exception exception) when (exception is VcInternalException or LibGit2SharpException)
        {
            try
            {
                using var current = new Repository(target);
                if (!string.IsNullOrWhiteSpace(mergedSha) && string.Equals(current.Head?.Tip?.Sha, mergedSha, StringComparison.OrdinalIgnoreCase))
                    RunGit("VALIDATED_MERGE_RECOVERY_REQUIRED", "Failed to restore master after validated merge failure.", "-C", target, "reset", "--hard", request.ExpectedTargetSha);
            }
            catch (Exception recovery)
            {
                throw new VcInternalException("VALIDATED_MERGE_RECOVERY_REQUIRED", $"Validated merge failed and recovery failed: {recovery.Message}", exception.Message);
            }
            throw;
        }
    }

    /// <summary>Write selected historical PLC XML blobs into a worktree without staging or committing.</summary>
    public static VcHistoricalPathsResult ApplyHistoricalPaths(string repoPath, string sourceSha, string[] paths)
    {
        EnsureRepo(repoPath);
        if (string.IsNullOrWhiteSpace(sourceSha)) throw new VcInternalException("COMMIT_REQUIRED", "sourceSha must not be empty.");
        if (paths is null || paths.Length == 0) throw new VcInternalException("SOURCE_PATHS_REQUIRED", "At least one source path is required.");
        using var repo = new Repository(repoPath);
        var commit = repo.Lookup<Commit>(sourceSha) ?? throw new VcInternalException("REF_NOT_FOUND", $"Commit '{sourceSha}' was not found.");
        var selected = paths.Select(SourcePathPolicy.Require).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        foreach (var path in selected)
        {
            var blob = commit[path]?.Target as Blob;
            if (blob is null)
                throw new VcInternalException("SOURCE_DELETE_UNSUPPORTED", $"Historical source '{path}' does not exist at '{commit.Sha}'.");
            var destination = Path.Combine(repo.Info.WorkingDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = blob.GetContentStream();
            using var output = File.Create(destination);
            input.CopyTo(output);
        }
        return new VcHistoricalPathsResult(repoPath, commit.Sha, selected);
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
        var files = EnumerateStagedPaths(repo)
            .Where(SourcePathPolicy.IsAllowed)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var signature = ResolveAuthor(repo, author);
        var commit = repo.Commit(message, signature, signature);
        return new VcCommitResult { Sha = commit.Sha, Message = message, Files = files };
    }

    /// <summary>Commit exactly the requested changed PLC source XML paths.</summary>
    public static VcCommitResult CommitSelected(
        string repoPath,
        string[] paths,
        string message,
        string? author = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new VcInternalException("MESSAGE_REQUIRED", "Commit message must not be empty.");
        if (paths == null || paths.Length == 0)
            throw new VcInternalException("SOURCE_PATHS_REQUIRED", "At least one PLC source XML path is required.");

        var selected = paths
            .Select(SourcePathPolicy.Require)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);
        var unchanged = selected
            .Where(path => ByteContentEquals(
                ReadTreeFile(repo.Head?.Tip?.Tree, path),
                ReadWorkingFile(repo, path)))
            .ToArray();
        if (unchanged.Length > 0)
        {
            throw new VcInternalException(
                "SOURCE_PATH_UNCHANGED",
                $"Selected PLC source path(s) have no HEAD-to-working-tree change: {string.Join(", ", unchanged)}.");
        }

        var indexSnapshot = CaptureIndex(repo);
        try
        {
            ResetIndexToHead(repo);
            Commands.Stage(repo, selected);
            var signature = ResolveAuthor(repo, author);
            var commit = repo.Commit(message, signature, signature);
            var files = GetCommitFiles(repo, commit);
            return new VcCommitResult { Sha = commit.Sha, Message = message, Files = files };
        }
        catch
        {
            RestoreIndex(indexSnapshot);
            throw;
        }
    }

    /// <summary>Create immutable validation evidence for the current repository HEAD.</summary>
    public static VcValidationEvidence CreateValidation(string repoPath, VcValidationEvidence? evidence)
    {
        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);
        var head = repo.Head?.Tip
            ?? throw new VcInternalException("HEAD_REQUIRED", "Validation requires a repository with a current HEAD.");
        if (!string.Equals(evidence?.CommitSha, head.Sha, StringComparison.OrdinalIgnoreCase))
        {
            throw new VcInternalException(
                "VALIDATION_HEAD_REQUIRED",
                $"Validation commit '{evidence?.CommitSha}' must be the current HEAD '{head.Sha}'.",
                "Refresh the history and validate the current commit only.");
        }

        return ValidationTagStore.Create(repo, evidence);
    }

    /// <summary>Read valid validation evidence for a commit; absent or invalid evidence returns null.</summary>
    public static VcValidationEvidence? GetValidation(string repoPath, string commitSha)
    {
        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);
        return ValidationTagStore.Read(repo, commitSha).Evidence;
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
            var normalizedFilePath = SourcePathPolicy.Require(filePath);
            var filter = new CommitFilter { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time };
            // Git tree paths are repository-relative and always use '/'. Keeping the
            // caller's path in that form is required by LibGit2Sharp's QueryBy on
            // Windows linked worktrees; converting to '\\' silently returns no commits.
            commits = repo.Commits.QueryBy(normalizedFilePath, filter).Take(cap).Select(c => c.Commit);
        }
        else
        {
            commits = repo.Commits.Take(cap);
        }

        var entries = commits.Select(c =>
        {
            var validation = ValidationTagStore.Read(repo, c.Sha);
            return new VcCommitEntry
            {
                Sha = c.Sha,
                Author = c.Author?.Name ?? "unknown",
                Message = c.MessageShort ?? c.Message ?? "",
                Timestamp = c.Author?.When.UtcDateTime.ToString("O") ?? "",
                Files = GetCommitFiles(repo, c),
                ValidationState = validation.State,
                EvidenceKind = validation.EvidenceKind,
            };
        }).ToArray();

        return new VcLogResult { RepoPath = repoPath, Commits = entries };
    }

    /// <summary>
    /// Show diff for a single file against HEAD, or between two arbitrary refs.
    /// Returns structured hunks parsed from the unified-diff output.
    /// </summary>
    public static VcDiffResult Diff(string repoPath, string filePath, string? oldSha = null, string? newSha = null)
    {
        var normalizedPath = SourcePathPolicy.Require(filePath);
        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);

        var head = repo.Head?.Tip;
        var oldCommit = !string.IsNullOrWhiteSpace(oldSha) ? RequireCommit(repo, oldSha) : head;
        var newCommit = !string.IsNullOrWhiteSpace(newSha) ? RequireCommit(repo, newSha) : null;
        var compareToWorkingTree = string.IsNullOrWhiteSpace(newSha);

        var oldBytes = ReadTreeFile(oldCommit?.Tree, normalizedPath);
        var newBytes = compareToWorkingTree
            ? ReadWorkingFile(repo, normalizedPath)
            : ReadTreeFile(newCommit!.Tree, normalizedPath);
        var oldTextAvailable = TryDecodeText(oldBytes, out var oldXml);
        var newTextAvailable = TryDecodeText(newBytes, out var newXml);
        var binary = !oldTextAvailable || !newTextAvailable;
        var summary = !binary && oldXml != null && newXml != null
            ? PlcXmlChangeSummary.Compare(oldXml, newXml)
            : PlcXmlChangeSummary.Compare(string.Empty, string.Empty);
        var hunks = binary
            ? Array.Empty<VcDiffHunk>()
            : BuildDiffHunks(
                oldXml == null ? null : NormalizeXmlForDiff(oldXml),
                newXml == null ? null : NormalizeXmlForDiff(newXml));

        return new VcDiffResult
        {
            RepoPath = repoPath,
            FilePath = normalizedPath,
            OldSha = oldCommit?.Sha,
            NewSha = compareToWorkingTree ? null : newCommit?.Sha,
            Binary = binary,
            Hunks = hunks,
            Summary = summary,
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
        return new VcCommitResult { Sha = commit.Sha, Message = msg, Files = GetCommitFiles(repo, commit) };
    }

    /// <summary>
    /// Restore file(s) from a given commit. Destructive: overwrites working tree.
    /// Uses CheckoutPaths for file-level restore.
    /// </summary>
    public static VcRestoreResult Restore(string repoPath, string? filePath = null, string? sourceSha = null)
    {
        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);

        Commit sourceCommit;
        if (!string.IsNullOrWhiteSpace(sourceSha))
        {
            sourceCommit = repo.Lookup<Commit>(sourceSha);
            if (sourceCommit == null)
                throw new VcInternalException("COMMIT_NOT_FOUND", $"Commit '{sourceSha}' was not found.");
        }
        else
        {
            if (repo.Head?.Tip == null)
                throw new VcInternalException("NO_COMMITS", "Repository has no commits yet.");
            sourceCommit = repo.Head.Tip;
        }

        var restored = new List<string>();
        var opts = new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force };

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var normalized = SourcePathPolicy.Require(filePath);
            repo.CheckoutPaths(sourceCommit.Sha, new[] { normalized }, opts);
            restored.Add(normalized);
        }
        else
        {
            var paths = EnumerateTreeFilePaths(sourceCommit.Tree)
                .Where(SourcePathPolicy.IsAllowed)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            if (paths.Length > 0)
            {
                repo.CheckoutPaths(sourceCommit.Sha, paths, opts);
                restored.AddRange(paths);
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

    private static void ResetIndexToHead(Repository repo)
    {
        if (repo.Head?.Tip is { } tip)
        {
            repo.Index.Replace(tip.Tree);
        }
        else
        {
            repo.Index.Clear();
        }

        repo.Index.Write();
    }

    private static IndexSnapshot CaptureIndex(Repository repo)
    {
        var indexPath = Path.Combine(repo.Info.Path, "index");
        return new IndexSnapshot(
            indexPath,
            File.Exists(indexPath) ? File.ReadAllBytes(indexPath) : null);
    }

    private static void RestoreIndex(IndexSnapshot snapshot)
    {
        if (snapshot.Content == null)
        {
            if (File.Exists(snapshot.Path))
                File.Delete(snapshot.Path);
            return;
        }

        File.WriteAllBytes(snapshot.Path, snapshot.Content);
    }

    private static bool ByteContentEquals(byte[]? left, byte[]? right) =>
        left == null ? right == null : right != null && left.AsSpan().SequenceEqual(right);

    private static string[] GetCommitFiles(Repository repo, Commit commit)
    {
        var parentTree = commit.Parents.FirstOrDefault()?.Tree;
        using var changes = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree);
        return changes
            .SelectMany(change => new[] { change.Path, change.OldPath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/'))
            .Where(SourcePathPolicy.IsAllowed)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static Commit RequireCommit(Repository repo, string reference)
    {
        var commit = repo.Lookup<Commit>(reference);
        if (commit == null)
        {
            throw new VcInternalException(
                "REF_NOT_FOUND",
                $"Git ref '{reference}' was not found.",
                "Use vc_log or vc_branches to select an existing commit or branch.");
        }

        return commit;
    }

    private static byte[]? ReadTreeFile(Tree? tree, string path)
    {
        var entry = tree?[path];
        if (entry?.Target is not Blob blob)
        {
            return null;
        }

        using var input = blob.GetContentStream();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static byte[]? ReadWorkingFile(Repository repo, string path)
    {
        var worktreeRoot = repo.Info.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(worktreeRoot))
        {
            throw new VcInternalException(
                "WORKTREE_REQUIRED",
                "A working-tree diff cannot be produced from a bare repository.");
        }

        var fullPath = Path.Combine(worktreeRoot, path.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
    }

    private static IEnumerable<string> EnumerateTreeFilePaths(Tree tree, string prefix = "")
    {
        foreach (var entry in tree)
        {
            var path = string.IsNullOrEmpty(prefix)
                ? entry.Name
                : $"{prefix}/{entry.Name}";
            if (entry.Target is Tree child)
            {
                foreach (var childPath in EnumerateTreeFilePaths(child, path))
                    yield return childPath;
            }
            else if (entry.Target is Blob)
            {
                yield return path;
            }
        }
    }

    private static bool TryDecodeText(byte[]? bytes, out string? text)
    {
        text = null;
        if (bytes == null)
        {
            return true;
        }

        try
        {
            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            {
                text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
                return true;
            }

            if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            {
                text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
                return true;
            }

            if (bytes.Contains((byte)0))
            {
                return false;
            }

            var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf
                ? 3
                : 0;
            text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = null;
            return false;
        }
    }

    private static string NormalizeXmlForDiff(string xml)
    {
        var normalized = xml.Replace("\r\n", "\n").Replace('\r', '\n');
        try
        {
            var document = XDocument.Parse(normalized, LoadOptions.PreserveWhitespace);
            if (document.Root?.Name.LocalName == "Document")
            {
                foreach (var created in document.Root.Elements()
                             .Where(element => element.Name.LocalName == "DocumentInfo")
                             .SelectMany(info => info.Elements()
                                 .Where(element => element.Name.LocalName == "Created"))
                             .ToArray())
                {
                    created.Remove();
                }
            }

            return document.ToString()
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
        }
        catch (XmlException)
        {
            return normalized;
        }
    }

    private static VcDiffHunk[] BuildDiffHunks(string? oldText, string? newText)
    {
        var oldLines = oldText?.Split('\n') ?? Array.Empty<string>();
        var newLines = newText?.Split('\n') ?? Array.Empty<string>();
        var edits = MyersDiff(oldLines, newLines);
        if (edits.All(edit => edit.Kind == LineEditKind.Context))
        {
            return Array.Empty<VcDiffHunk>();
        }

        const int contextSize = 3;
        var hunks = new List<VcDiffHunk>();
        var scan = 0;
        while (scan < edits.Count)
        {
            var firstChange = edits.FindIndex(scan, edit => edit.Kind != LineEditKind.Context);
            if (firstChange < 0)
                break;

            var start = Math.Max(0, firstChange - contextSize);
            var lastChange = firstChange;
            var search = firstChange + 1;
            while (search < edits.Count)
            {
                var nextChange = edits.FindIndex(search, edit => edit.Kind != LineEditKind.Context);
                if (nextChange < 0 || nextChange - lastChange > contextSize * 2 + 1)
                    break;
                lastChange = nextChange;
                search = nextChange + 1;
            }

            var end = Math.Min(edits.Count, lastChange + contextSize + 1);
            var first = edits[start];
            hunks.Add(new VcDiffHunk
            {
                OldStart = first.OldLine,
                NewStart = first.NewLine,
                Lines = edits.Skip(start).Take(end - start).Select(edit => new VcDiffLine
                {
                    Type = edit.Kind switch
                    {
                        LineEditKind.Addition => "addition",
                        LineEditKind.Deletion => "deletion",
                        _ => "context",
                    },
                    Content = edit.Content,
                }).ToArray(),
            });
            scan = end;
        }

        return hunks.ToArray();
    }

    private static List<LineEdit> MyersDiff(string[] oldLines, string[] newLines)
    {
        var oldCount = oldLines.Length;
        var newCount = newLines.Length;
        var max = oldCount + newCount;
        var offset = max + 1;
        var frontier = Enumerable.Repeat(-1, max * 2 + 3).ToArray();
        frontier[offset + 1] = 0;
        var trace = new List<int[]>();

        for (var distance = 0; distance <= max; distance++)
        {
            for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
            {
                var index = offset + diagonal;
                var x = diagonal == -distance ||
                        (diagonal != distance && frontier[index - 1] < frontier[index + 1])
                    ? frontier[index + 1]
                    : frontier[index - 1] + 1;
                var y = x - diagonal;
                while (x < oldCount && y < newCount &&
                       string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                frontier[index] = x;
                if (x >= oldCount && y >= newCount)
                {
                    trace.Add((int[])frontier.Clone());
                    return BacktrackMyers(trace, oldLines, newLines, offset);
                }
            }

            trace.Add((int[])frontier.Clone());
        }

        throw new InvalidOperationException("Unable to calculate line diff.");
    }

    private static List<LineEdit> BacktrackMyers(
        IReadOnlyList<int[]> trace,
        string[] oldLines,
        string[] newLines,
        int offset)
    {
        var reversed = new List<(LineEditKind Kind, string Content)>();
        var x = oldLines.Length;
        var y = newLines.Length;

        for (var distance = trace.Count - 1; distance > 0; distance--)
        {
            var previous = trace[distance - 1];
            var diagonal = x - y;
            var previousDiagonal = diagonal == -distance ||
                                   (diagonal != distance &&
                                    previous[offset + diagonal - 1] < previous[offset + diagonal + 1])
                ? diagonal + 1
                : diagonal - 1;
            var previousX = previous[offset + previousDiagonal];
            var previousY = previousX - previousDiagonal;

            while (x > previousX && y > previousY)
            {
                reversed.Add((LineEditKind.Context, oldLines[x - 1]));
                x--;
                y--;
            }

            if (x == previousX)
            {
                reversed.Add((LineEditKind.Addition, newLines[y - 1]));
                y--;
            }
            else
            {
                reversed.Add((LineEditKind.Deletion, oldLines[x - 1]));
                x--;
            }
        }

        while (x > 0 && y > 0)
        {
            reversed.Add((LineEditKind.Context, oldLines[x - 1]));
            x--;
            y--;
        }
        while (x > 0)
            reversed.Add((LineEditKind.Deletion, oldLines[--x]));
        while (y > 0)
            reversed.Add((LineEditKind.Addition, newLines[--y]));

        reversed.Reverse();
        var oldLine = 1;
        var newLine = 1;
        return reversed.Select(edit =>
        {
            var result = new LineEdit(edit.Kind, edit.Content, oldLine, newLine);
            if (edit.Kind != LineEditKind.Addition) oldLine++;
            if (edit.Kind != LineEditKind.Deletion) newLine++;
            return result;
        }).ToList();
    }

    private sealed record IndexSnapshot(string Path, byte[]? Content);
    private sealed record LineEdit(LineEditKind Kind, string Content, int OldLine, int NewLine);
    private enum LineEditKind { Context, Addition, Deletion }

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
