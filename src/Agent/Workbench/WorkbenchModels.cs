namespace Agent.Workbench;

public static class WorkbenchSchema
{
    public const string CurrentVersion = "1.0";
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
    IReadOnlyList<WorkbenchWorktreeRegistration> Worktrees);

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
    string? LastReconciliationCommit);

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
