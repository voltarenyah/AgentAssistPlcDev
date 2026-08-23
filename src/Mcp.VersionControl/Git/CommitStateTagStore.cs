using System.Text.Json;
using System.Text.Json.Serialization;
using LibGit2Sharp;

namespace Mcp.VersionControl.Git;

/// <summary>Immutable, app-owned annotated Git tags recording the TIA device checksum state
/// at the time an ordinary source commit was made. Every commit that passes through
/// <see cref="WorkbenchCoordinator.ApplyTiaSynchronizationAsync"/> gets one of these tags
/// so the device checksums that produced the commit are discoverable later.</summary>
internal static class CommitStateTagStore
{
    public const string TagPrefix = "tia-state/";
    public const string SchemaVersion = "1.0";

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

    /// <summary>Create an immutable annotated tag on <paramref name="commitSha"/> recording
    /// the per-device checksums that were observed in TIA when the commit was created.</summary>
    public static VcCommitStateEvidence Create(
        Repository repository,
        string commitSha,
        string workbenchId,
        IReadOnlyList<VcCommitStateDevice> devices)
    {
        var commit = RequireCommit(repository, commitSha);
        var tagName = TagName(commit.Sha);
        if (repository.Tags[tagName] != null)
        {
            throw new VcInternalException(
                "STATE_TAG_EXISTS",
                $"TIA state tag already exists for commit '{commit.Sha}'.");
        }

        var normalized = NormalizeAndValidate(commitSha, workbenchId, devices);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        repository.ApplyTag(
            tagName,
            commit.Sha,
            new Signature("Workbench", "assistant@plc-assistant.local", DateTimeOffset.UtcNow),
            json);
        return normalized;
    }

    /// <summary>Read the TIA state evidence for a commit, or null if no tag exists.</summary>
    public static VcCommitStateEvidence? Read(Repository repository, string commitSha)
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
            var evidence = JsonSerializer.Deserialize<VcCommitStateEvidence>(tag.Annotation.Message, JsonOptions);
            if (evidence == null)
            {
                return null;
            }

            return NormalizeAndValidate(evidence.CommitSha, evidence.WorkbenchId, evidence.Devices);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (VcInternalException)
        {
            return null;
        }
    }

    private static VcCommitStateEvidence NormalizeAndValidate(
        string commitSha,
        string workbenchId,
        IReadOnlyList<VcCommitStateDevice> devices)
    {
        if (string.IsNullOrWhiteSpace(commitSha))
            throw new VcInternalException("COMMIT_REQUIRED", "commitSha must not be empty.");
        if (string.IsNullOrWhiteSpace(workbenchId))
            throw new VcInternalException("WORKBENCH_REQUIRED", "workbenchId must not be empty.");
        if (devices == null || devices.Count == 0)
            throw new VcInternalException("DEVICES_REQUIRED", "At least one device checksum must be recorded.");

        var normalizedDevices = devices
            .Select(device =>
            {
                if (device == null)
                    throw new VcInternalException("DEVICE_INVALID", "Device entry must not be null.");
                if (string.IsNullOrWhiteSpace(device.DeviceId))
                    throw new VcInternalException("DEVICE_INVALID", "deviceId must not be empty.");
                if (string.IsNullOrWhiteSpace(device.PlcName))
                    throw new VcInternalException("DEVICE_INVALID", "plcName must not be empty.");
                if (string.IsNullOrWhiteSpace(device.ProjectChecksum))
                    throw new VcInternalException("DEVICE_INVALID", "projectChecksum must not be empty.");
                return device;
            })
            .OrderBy(device => device.DeviceId, StringComparer.Ordinal)
            .ToArray();

        if (normalizedDevices.Select(d => d.DeviceId).Distinct(StringComparer.Ordinal).Count() != normalizedDevices.Length)
            throw new VcInternalException("DEVICE_INVALID", "Duplicate device IDs in state evidence.");

        return new VcCommitStateEvidence(
            SchemaVersion,
            commitSha.ToLowerInvariant(),
            workbenchId,
            normalizedDevices);
    }

    private static Commit RequireCommit(Repository repository, string commitSha)
    {
        var commit = repository.Lookup<Commit>(commitSha);
        if (commit == null)
            throw new VcInternalException("REF_NOT_FOUND", $"Commit '{commitSha}' was not found.");
        return commit;
    }
}

/// <summary>Evidence recorded in a <c>tia-state/{sha}</c> annotated tag.</summary>
public sealed record VcCommitStateEvidence(
    string SchemaVersion,
    string CommitSha,
    string WorkbenchId,
    IReadOnlyList<VcCommitStateDevice> Devices);

/// <summary>Per-device checksum recorded at commit time.</summary>
public sealed record VcCommitStateDevice(
    string DeviceId,
    string PlcName,
    string ProjectChecksum);
