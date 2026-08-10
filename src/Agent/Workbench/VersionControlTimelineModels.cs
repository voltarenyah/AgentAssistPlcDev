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
    long? SvnRevision);

public sealed record VersionControlTimelineSvnRevision(
    long Revision,
    string Author,
    string Message,
    string Timestamp,
    string? TiaChecksum,
    string GitCommitSha);

internal sealed class TimelineSvnLogResult
{
    public TimelineSvnLogEntry[] Entries { get; set; } = Array.Empty<TimelineSvnLogEntry>();
}

internal sealed class TimelineSvnLogEntry
{
    public long Revision { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime Time { get; set; }
}
