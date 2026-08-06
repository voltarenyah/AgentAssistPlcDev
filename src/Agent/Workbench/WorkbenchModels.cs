using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Workbench;

public static class WorkbenchSchema
{
    public const string CurrentVersion = "1.2";

    /// <summary>Versions readers accept; 1.0 files predate the landing-page fields and
    /// deserialize with defaults (null purpose/owner, ongoing status). 1.1 files predate the
    /// SVN native store and deserialize with null SvnRepositoryPath/provenance fields — SVN
    /// features stay unavailable for those workbenches (no migration).</summary>
    public static readonly IReadOnlyList<string> SupportedVersions = ["1.0", "1.1", CurrentVersion];
}

public sealed record WorkbenchMetadata(
    string SchemaVersion,
    string WorkbenchId,
    string Name,
    string CreatedAt,
    string RootPath,
    string RepositoryPath,
    string? EngineeringProjectId,
    string? SourceProjectPath,
    IReadOnlyList<WorkbenchWorktreeRegistration> Worktrees,
    string? Purpose = null,
    string? Owner = null,
    /// <summary>&lt;root&gt;/repository.svn — the local SVN native store (1.2+; null for 1.1).</summary>
    string? SvnRepositoryPath = null,
    /// <summary>Provenance only: the origin .ap17 the workbench was imported from (1.2+).</summary>
    string? OriginProjectPath = null,
    string? OriginImportedAt = null,
    /// <summary>Operational TIA project inside the worktree's tia/ working copy (1.2+).</summary>
    string? ManagedTiaProjectPath = null);

public sealed record WorkbenchWorktreeRegistration(
    string WorktreeId,
    string Name,
    string Branch,
    string RelativePath);

public sealed record WorktreeMetadata(
    string SchemaVersion,
    string WorktreeId,
    string WorkbenchId,
    string Name,
    string Branch,
    string CreatedAt,
    string? BaseCommit,
    string? EngineeringProjectId,
    string? SourceProjectPath,
    IReadOnlyList<string> DeviceIds,
    string? LastReconciliationCommit,
    string? Purpose = null,
    string? Owner = null,
    WorktreeStatus Status = WorktreeStatus.Ongoing,
    DateTimeOffset? FinishedUtc = null,
    /// <summary>Operational TIA project inside this worktree's tia/ working copy (1.2+);
    /// null on 1.1 worktrees, where SourceProjectPath stays the operational path.</summary>
    string? ManagedTiaProjectPath = null,
    /// <summary>This worktree's own SVN branch (1.2+ feature worktrees), e.g.
    /// "^/native/branches/feature-x"; null on master and on 1.1 worktrees.</summary>
    string? SvnUrl = null,
    /// <summary>The ^/native/main revision this feature's SVN branch was copied from.</summary>
    long? BaseSvnRevision = null);

[JsonConverter(typeof(WorktreeStatusJsonConverter))]
public enum WorktreeStatus
{
    Ongoing,
    Finished,
}

[JsonConverter(typeof(WorktreeTaskStatusJsonConverter))]
public enum WorktreeTaskStatus
{
    Todo,
    InProgress,
    Done,
}

/// <summary>camelCase string enum wire format ("ongoing"/"finished") shared by
/// AtomicJsonStore persistence and ASP.NET responses.</summary>
public sealed class WorktreeStatusJsonConverter()
    : JsonStringEnumConverter<WorktreeStatus>(JsonNamingPolicy.CamelCase);

public sealed class WorktreeTaskStatusJsonConverter()
    : JsonStringEnumConverter<WorktreeTaskStatus>(JsonNamingPolicy.CamelCase);

/// <summary>A worktree-scoped task: which PLC element needs modification, its status,
/// and the modification plan. ElementRefs are display/jump hints, not foreign keys.</summary>
public sealed record WorktreeTask(
    string TaskId,
    string Title,
    string? Details,
    WorktreeTaskStatus Status,
    string[] ElementRefs,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? DoneUtc);

/// <summary>Persisted as worktrees/&lt;name&gt;/tasks.json; kept separate from worktree.json
/// because tasks churn on every edit while the metadata file stays small and stable.</summary>
public sealed record WorktreeTaskList(int Version, List<WorktreeTask> Tasks);

public sealed record DeviceMetadata(
    string SchemaVersion,
    string DeviceId,
    string WorktreeId,
    string PlcName,
    string EngineeringIdentity,
    string? LastExportChecksum,
    string? LastExportUtc,
    string? LastReconciliationCommit,
    KnowledgeState Knowledge,
    IReadOnlyList<DeviceImportRecord> Imports);

public sealed record KnowledgeState(
    bool Stale,
    IReadOnlyDictionary<string, string> AppliedOverlayHashes,
    string? UpdatedAt,
    bool BaselineStale = false);

public sealed record DeviceImportRecord(
    string RelativePath,
    string ImportedAt,
    bool ImportSucceeded,
    string CompileState,
    IReadOnlyList<string> Warnings,
    string? Error);

public sealed record TiaSynchronizationResult(
    string ComparisonId,
    IReadOnlyList<string> PendingPaths,
    string? CommitSha = null);

public sealed record WorkbenchCommitResult(
    string Sha,
    string Message,
    IReadOnlyList<string> Files);

public sealed class CoordinatorGitCommitResult
{
    public string Sha { get; set; } = string.Empty;
}

public sealed class CoordinatorSvnInitResult
{
    public string RepositoryPath { get; set; } = string.Empty;
    public string RepositoryUri { get; set; } = string.Empty;
}

public sealed class CoordinatorSvnCommitResult
{
    public bool Committed { get; set; }
    public long Revision { get; set; }
}

public sealed class CoordinatorSvnStatusResult
{
    public bool IsClean { get; set; }
}

public sealed class CoordinatorSaveProjectAsResult
{
    public string? ManagedProjectPath { get; set; }
}

public sealed record DeviceContext(
    string WorkbenchId,
    string WorktreeId,
    string DeviceId,
    string WorkbenchRoot,
    string WorktreeRoot,
    string DeviceRoot,
    string SourceRoot,
    string StagingRoot,
    string KnowledgeDbPath);

public sealed record HardwareConfigurationReloadResult(
    string RootPath,
    int ArtifactCount,
    int DeviceCount,
    string CommitSha,
    IReadOnlyList<string>? Warnings = null);

public sealed record HardwareConfigurationCompareArtifact(
    string Scope,
    string? DeviceName,
    string State);

public sealed record HardwareConfigurationCompareResult(
    string State,
    string RootPath,
    IReadOnlyList<HardwareConfigurationCompareArtifact> Artifacts,
    string Message,
    IReadOnlyList<string>? Warnings = null,
    string? StagingPath = null);

public sealed record HardwareConfigurationOverwriteResult(
    string RootPath,
    int ArtifactCount,
    string CommitSha);
