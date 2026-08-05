using Agent.Mcp;

namespace Agent.Workbench;

public sealed record RollbackFeatureResult(
    string WorktreeId,
    string Branch,
    string HistoricalSha,
    IReadOnlyList<string> Paths);

/// <summary>Applies selected historical XML blobs to an already-created feature checkout.</summary>
public sealed class RollbackFeatureService
{
    private readonly IMcpToolCaller versionControl;

    public RollbackFeatureService(IMcpToolCaller versionControl) =>
        this.versionControl = versionControl ?? throw new ArgumentNullException(nameof(versionControl));

    public async Task<RollbackFeatureResult> ApplyAsync(
        string featureRoot,
        string historicalSha,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
    {
        var result = await versionControl.CallAsync<HistoricalPathsDto>(
            "vc_apply_historical_paths",
            new { repoPath = featureRoot, sourceSha = historicalSha, paths },
            cancellationToken).ConfigureAwait(false);
        return new RollbackFeatureResult(string.Empty, string.Empty, result.SourceSha, result.Applied);
    }

    private sealed class HistoricalPathsDto
    {
        public string RepoPath { get; set; } = string.Empty;
        public string SourceSha { get; set; } = string.Empty;
        public string[] Applied { get; set; } = Array.Empty<string>();
    }
}
