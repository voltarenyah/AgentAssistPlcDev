using System.Text.Json;
using System.Text.Json.Serialization;
using LibGit2Sharp;

namespace Mcp.VersionControl.Git;

/// <summary>Immutable, app-owned annotated Git tags marking a commit as an untrackable
/// change: a TIA change that leaves no git-tracked file diff and is committed message-only.
/// The marker lets timelines flag the commit so users know the TIA state is not restorable
/// from git content until an SVN savepoint persists it.</summary>
internal static class UntrackableChangeTagStore
{
    public const string TagPrefix = "untrackable-change/";
    public const string SchemaVersion = "1.0";
    public const string Kind = "untrackable-change";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string TagName(string commitSha)
    {
        if (string.IsNullOrWhiteSpace(commitSha))
            throw new VcInternalException("COMMIT_REQUIRED", "commitSha must not be empty.");

        return TagPrefix + commitSha.ToLowerInvariant();
    }

    /// <summary>Create an immutable annotated tag on <paramref name="commitSha"/> marking it
    /// as an untrackable-change commit.</summary>
    public static VcUntrackableChangeMarker Create(Repository repository, string commitSha)
    {
        var commit = RequireCommit(repository, commitSha);
        var tagName = TagName(commit.Sha);
        if (repository.Tags[tagName] != null)
        {
            throw new VcInternalException(
                "UNTRACKABLE_CHANGE_TAG_EXISTS",
                $"Untrackable-change tag already exists for commit '{commit.Sha}'.");
        }

        var marker = new VcUntrackableChangeMarker(SchemaVersion, commit.Sha.ToLowerInvariant(), Kind);
        var json = JsonSerializer.Serialize(marker, JsonOptions);
        repository.ApplyTag(
            tagName,
            commit.Sha,
            new Signature("Workbench", "assistant@plc-assistant.local", DateTimeOffset.UtcNow),
            json);
        return marker;
    }

    /// <summary>Read the untrackable-change marker for a commit, or null when the tag is
    /// missing, not annotated, targets another commit, or carries an invalid payload.</summary>
    public static VcUntrackableChangeMarker? Read(Repository repository, string commitSha)
    {
        var commit = RequireCommit(repository, commitSha);
        var tag = repository.Tags[TagName(commit.Sha)];
        if (tag == null)
        {
            return null;
        }

        if (!tag.IsAnnotated || tag.Annotation == null || tag.Target is not Commit target ||
            !string.Equals(target.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<VcUntrackableChangeMarker>(tag.Annotation.Message, JsonOptions);
            if (marker == null
                || !string.Equals(marker.SchemaVersion, SchemaVersion, StringComparison.Ordinal)
                || !string.Equals(marker.Kind, Kind, StringComparison.Ordinal)
                || !string.Equals(marker.CommitSha, commit.Sha, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return marker;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Commit RequireCommit(Repository repository, string commitSha)
    {
        var commit = repository.Lookup<Commit>(commitSha);
        if (commit == null)
            throw new VcInternalException("REF_NOT_FOUND", $"Commit '{commitSha}' was not found.");
        return commit;
    }
}

/// <summary>Marker recorded in an <c>untrackable-change/{sha}</c> annotated tag.</summary>
public sealed record VcUntrackableChangeMarker(
    string SchemaVersion,
    string CommitSha,
    string Kind);
