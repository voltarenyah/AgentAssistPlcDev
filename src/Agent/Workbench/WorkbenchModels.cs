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
