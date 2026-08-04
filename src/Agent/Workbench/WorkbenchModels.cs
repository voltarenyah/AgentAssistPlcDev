using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Workbench;

public static class WorkbenchSchema
{
    public const string CurrentVersion = "1.1";

    /// <summary>Versions readers accept; 1.0 files predate the landing-page fields and
    /// deserialize with defaults (null purpose/owner, ongoing status).</summary>
    public static readonly IReadOnlyList<string> SupportedVersions = ["1.0", CurrentVersion];
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
    string? Owner = null);

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
    DateTimeOffset? FinishedUtc = null);

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

public sealed record DeviceContext(
    string WorkbenchId,
    string WorktreeId,
    string DeviceId,
    string WorkbenchRoot,
    string WorktreeRoot,
    string DeviceRoot,
    string ExportedSourceRoot,
    string ModifiedSourceRoot,
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
