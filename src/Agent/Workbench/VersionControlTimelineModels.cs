namespace Agent.Workbench;

public sealed record VersionControlTimelineResult(
    IReadOnlyList<VersionControlTimelineGitCommit> GitCommits,
    IReadOnlyList<VersionControlTimelineSvnRevision> SvnRevisions,
    bool HasMore);

public sealed record VersionControlTimelineGitCommit(
    string Sha,
    string Author,
    string Message,
    string Timestamp,
    IReadOnlyList<string> Files,
    string? TiaChecksum,
    long? SvnRevision,
    bool UntrackableChange,
    string? TiaContentFingerprint = null,
    bool SafetyChange = false);

public sealed record VersionControlTimelineSvnRevision(
    long Revision,
    string Author,
    string Message,
    string Timestamp,
    string? TiaChecksum,
    string GitCommitSha,
    string? TiaContentFingerprint = null);

internal sealed class TimelineSvnLogResult
{
    public TimelineSvnLogEntry[] Entries { get; set; } = Array.Empty<TimelineSvnLogEntry>();
}

/// <summary>Result of the <c>vc_untrackable_change_get</c> tool: whether the commit carries
/// an untrackable-change marker tag.</summary>
internal sealed class TimelineUntrackableChangeResult
{
    public bool UntrackableChange { get; set; }
}

internal sealed class TimelineSafetyChangeResult
{
    public bool SafetyChange { get; set; }
}

internal sealed class TimelineSvnLogEntry
{
    public long Revision { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime Time { get; set; }
}
