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

    /// <summary>Show working-tree status.</summary>
    public static VcStatusResult Status(string repoPath)
    {
        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);
        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
        });

        var branch = repo.Head?.FriendlyName ?? "HEAD";
        var entries = new List<VcStatusEntry>();

        foreach (var entry in status)
        {
            entries.Add(new VcStatusEntry
            {
                FilePath = entry.FilePath.Replace('\\', '/'),
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

    /// <summary>Stage files. When paths is null or empty, stages all changes.</summary>
    public static VcAddResult Add(string repoPath, string[]? paths = null)
    {
        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);

        if (paths is { Length: > 0 })
        {
            foreach (var p in paths)
            {
                repo.Index.Add(p.Replace('/', '\\'));
            }
        }
        else
        {
            Commands.Stage(repo, "*");
        }

        repo.Index.Write();
        return new VcAddResult { Staged = repo.RetrieveStatus().Staged.Count() };
    }

    /// <summary>Commit staged changes. Returns the commit SHA.</summary>
    public static VcCommitResult Commit(string repoPath, string message, string? author = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new VcInternalException("MESSAGE_REQUIRED", "Commit message must not be empty.");

        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);
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
            commits = repo.Commits.QueryBy(filePath.Replace('/', '\\'), filter).Take(cap).Select(c => c.Commit);
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

        var normalizedPath = filePath.Replace('/', '\\');

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
            e.Path.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>Stage all changes and commit with an auto-generated or given message.</summary>
    public static VcCommitResult Snapshot(string repoPath, string? message = null)
    {
        EnsureRepo(repoPath);
        using var repo = new Repository(repoPath);

        // Stage all changes (modified, deleted, renamed)
        Commands.Stage(repo, "*");
        // Also explicitly add untracked files (Commands.Stage glob may not match on all platforms)
        var status = repo.RetrieveStatus();
        foreach (var entry in status.Untracked)
        {
            repo.Index.Add(entry.FilePath);
        }
        repo.Index.Write();

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
