using System.Text.Json;
using System.Text.Json.Serialization;
using LibGit2Sharp;

namespace Mcp.VersionControl.Git;

internal static class SafetyChangeTagStore
{
    public const string TagPrefix = "safety-change/";
    private const string SchemaVersion = "1.0";
    private const string Kind = "safety-change";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static void Create(Repository repository, string commitSha)
    {
        var commit = RequireCommit(repository, commitSha);
        var tagName = TagPrefix + commit.Sha.ToLowerInvariant();
        if (repository.Tags[tagName] != null) throw new VcInternalException("SAFETY_CHANGE_TAG_EXISTS", $"Safety-change tag already exists for commit '{commit.Sha}'.");
        var payload = JsonSerializer.Serialize(new VcSafetyChangeMarker(SchemaVersion, commit.Sha.ToLowerInvariant(), Kind), JsonOptions);
        repository.ApplyTag(tagName, commit.Sha, new Signature("Workbench", "assistant@plc-assistant.local", DateTimeOffset.UtcNow), payload);
    }

    public static bool Exists(Repository repository, string commitSha)
    {
        var commit = RequireCommit(repository, commitSha);
        var tag = repository.Tags[TagPrefix + commit.Sha.ToLowerInvariant()];
        if (tag?.IsAnnotated != true || tag.Annotation == null || tag.Target is not Commit target || !string.Equals(target.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var payload = JsonSerializer.Deserialize<VcSafetyChangeMarker>(tag.Annotation.Message, JsonOptions);
            return payload is not null && payload.SchemaVersion == SchemaVersion && payload.Kind == Kind && string.Equals(payload.CommitSha, commit.Sha, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    private static Commit RequireCommit(Repository repository, string commitSha) => repository.Lookup<Commit>(commitSha) ?? throw new VcInternalException("REF_NOT_FOUND", $"Commit '{commitSha}' was not found.");
}

public sealed record VcSafetyChangeMarker(string SchemaVersion, string CommitSha, string Kind);
