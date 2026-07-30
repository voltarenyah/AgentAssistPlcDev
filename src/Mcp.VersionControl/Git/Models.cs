namespace Mcp.VersionControl.Git;

/// <summary>Result of vc_status.</summary>
public sealed class VcStatusResult
{
    public string RepoPath { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public VcStatusEntry[] Entries { get; set; } = Array.Empty<VcStatusEntry>();
}

public sealed class VcStatusEntry
{
    public string FilePath { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;  // "Untracked", "Modified", "Added", "Deleted", "Staged", "RenamedInWorkdir"
    public bool Staged { get; set; }
}

/// <summary>Result of vc_log — one commit.</summary>
public sealed class VcCommitEntry
{
    public string Sha { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public string[] Files { get; set; } = Array.Empty<string>();
}

/// <summary>Result of vc_diff — structured hunks for one file.</summary>
public sealed class VcDiffResult
{
    public string RepoPath { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? OldSha { get; set; }
    public string? NewSha { get; set; }
    public bool Binary { get; set; }
    public VcDiffHunk[] Hunks { get; set; } = Array.Empty<VcDiffHunk>();
}

public sealed class VcDiffHunk
{
    public int OldStart { get; set; }
    public int NewStart { get; set; }
    public VcDiffLine[] Lines { get; set; } = Array.Empty<VcDiffLine>();
}

public sealed class VcDiffLine
{
    public string Type { get; set; } = string.Empty;  // "context", "addition", "deletion"
    public string Content { get; set; } = string.Empty;
}

/// <summary>Result of vc_branches — one branch.</summary>
public sealed class VcBranchInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsHead { get; set; }
    public string Sha { get; set; } = string.Empty;
    public string? Upstream { get; set; }
}

/// <summary>Result of vc_log.</summary>
public sealed class VcLogResult
{
    public string RepoPath { get; set; } = string.Empty;
    public VcCommitEntry[] Commits { get; set; } = Array.Empty<VcCommitEntry>();
}

/// <summary>Result of vc_init.</summary>
public sealed class VcInitResult
{
    public string RepoPath { get; set; } = string.Empty;
    public bool Initialized { get; set; }
    public bool ExistingRepo { get; set; }
}

/// <summary>Result of vc_init_shared.</summary>
public sealed class VcSharedInitResult
{
    public string WorkbenchRoot { get; set; } = string.Empty;
    public string RepositoryPath { get; set; } = string.Empty;
    public string MasterWorktreePath { get; set; } = string.Empty;
    public bool ExistingRepository { get; set; }
}

/// <summary>Result of vc_add_worktree.</summary>
public sealed class VcWorktreeResult
{
    public string RepositoryPath { get; set; } = string.Empty;
    public string WorktreePath { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string Sha { get; set; } = string.Empty;
}

/// <summary>Result of vc_remove_worktree.</summary>
public sealed class VcWorktreeRemoveResult
{
    public string RepositoryPath { get; set; } = string.Empty;
    public string WorktreePath { get; set; } = string.Empty;
    public bool Removed { get; set; }
}

/// <summary>One linked worktree in vc_worktrees.</summary>
public sealed class VcWorktreeInfo
{
    public string WorktreePath { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string Sha { get; set; } = string.Empty;
    public bool Detached { get; set; }
    public bool Locked { get; set; }
}

/// <summary>Result of vc_worktrees.</summary>
public sealed class VcWorktreeListResult
{
    public string RepositoryPath { get; set; } = string.Empty;
    public VcWorktreeInfo[] Worktrees { get; set; } = Array.Empty<VcWorktreeInfo>();
}

/// <summary>Result of vc_merge.</summary>
public sealed class VcMergeResult
{
    public string TargetWorktreePath { get; set; } = string.Empty;
    public string TargetBranch { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string SourceSha { get; set; } = string.Empty;
    public string Sha { get; set; } = string.Empty;
    public bool Merged { get; set; }
}

/// <summary>Result of vc_add.</summary>
public sealed class VcAddResult
{
    public int Staged { get; set; }
}

/// <summary>Result of vc_commit / vc_snapshot.</summary>
public sealed class VcCommitResult
{
    public string Sha { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>Result of vc_restore.</summary>
public sealed class VcRestoreResult
{
    public string[] Restored { get; set; } = Array.Empty<string>();
}

/// <summary>Result of vc_branches.</summary>
public sealed class VcBranchesResult
{
    public VcBranchInfo[] Branches { get; set; } = Array.Empty<VcBranchInfo>();
}

/// <summary>Result of vc_checkout.</summary>
public sealed class VcCheckoutResult
{
    public string Branch { get; set; } = string.Empty;
    public string Sha { get; set; } = string.Empty;
}

/// <summary>Result of vc_config.</summary>
public sealed class VcConfigResult
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string Operation { get; set; } = string.Empty;  // "read", "set"
}
