namespace Agent.Workbench;

public sealed record FeatureImportObject(
    string DeviceId,
    string PlcName,
    string RelativePath,
    string FeatureFingerprint,
    bool Importable,
    string? Reason);

public sealed record FeatureImportPlan(
    string PlanId,
    string WorkbenchId,
    string FeatureWorktreeId,
    string FeatureSha,
    string MasterSha,
    string ComparisonId,
    IReadOnlyList<FeatureImportObject> Objects);

public enum FeatureImportState
{
    Pending,
    Imported,
    Failed,
    KeptAfterCompileFailure,
    RolledBack,
}

public sealed record FeatureImportOutcome(
    string DeviceId,
    string RelativePath,
    FeatureImportState State,
    string? Error,
    IReadOnlyList<string> Warnings);

public sealed record FeatureImportSession(
    string SessionId,
    string PlanId,
    string FeatureSha,
    string MasterSha,
    string StartedAt,
    IReadOnlyList<FeatureImportOutcome> Objects);

internal sealed class FeaturePreviewDto
{
    public string TargetBranch { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string MergeBaseSha { get; set; } = string.Empty;
    public string TargetSha { get; set; } = string.Empty;
    public string SourceSha { get; set; } = string.Empty;
    public string? CandidateTreeSha { get; set; }
    public bool HasConflicts { get; set; }
    public string[] ConflictPaths { get; set; } = Array.Empty<string>();
    public string[] FeaturePaths { get; set; } = Array.Empty<string>();
    public FeaturePreviewObjectDto[] Objects { get; set; } = Array.Empty<FeaturePreviewObjectDto>();
}

internal sealed class FeaturePreviewObjectDto
{
    public string FilePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Length { get; set; }
}
