using System.Text.Json;
using System.Text.Json.Serialization;
using LibGit2Sharp;

namespace Mcp.VersionControl.Git;

/// <summary>Immutable, app-owned annotated Git tags containing TIA validation evidence.</summary>
internal static class ValidationTagStore
{
    public const string TagPrefix = "tia-validation/";
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

    public static VcValidationEvidence Create(Repository repository, VcValidationEvidence? evidence)
    {
        var commit = RequireCommit(repository, evidence?.CommitSha);
        var normalized = NormalizeAndValidate(evidence, commit.Sha);
        var tagName = TagName(commit.Sha);
        if (repository.Tags[tagName] != null)
        {
            throw new VcInternalException(
                "VALIDATION_EXISTS",
                $"Validation evidence already exists for commit '{commit.Sha}'.",
                "Validation evidence is immutable; create a new commit before validating again.");
        }

        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        repository.ApplyTag(
            tagName,
            commit.Sha,
            new Signature(normalized.ConfirmedBy, ResolveEmail(normalized.ConfirmedBy), DateTimeOffset.UtcNow),
            json);
        return normalized;
    }

    public static ValidationTagRead Read(Repository repository, string commitSha)
    {
        var commit = RequireCommit(repository, commitSha);
        var tag = repository.Tags[TagName(commit.Sha)];
        if (tag == null)
        {
            return new ValidationTagRead(VcValidationState.Unlabeled, null, null);
        }

        if (!tag.IsAnnotated || tag.Annotation == null || tag.Target is not Commit target ||
            !string.Equals(target.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase))
        {
            return new ValidationTagRead(
                VcValidationState.Invalid,
                TryReadEvidenceKind(tag.Annotation?.Message),
                null);
        }

        try
        {
            var evidence = JsonSerializer.Deserialize<VcValidationEvidence>(tag.Annotation.Message, JsonOptions);
            if (evidence == null)
            {
                return new ValidationTagRead(VcValidationState.Invalid, null, null);
            }

            var normalized = NormalizeAndValidate(evidence, commit.Sha);
            return new ValidationTagRead(VcValidationState.Validated, normalized.EvidenceKind, normalized);
        }
        catch (VcInternalException)
        {
            return new ValidationTagRead(
                VcValidationState.Invalid,
                TryReadEvidenceKind(tag.Annotation.Message),
                null);
        }
        catch (JsonException)
        {
            return new ValidationTagRead(
                VcValidationState.Invalid,
                TryReadEvidenceKind(tag.Annotation.Message),
                null);
        }
        catch (ArgumentException)
        {
            return new ValidationTagRead(
                VcValidationState.Invalid,
                TryReadEvidenceKind(tag.Annotation.Message),
                null);
        }
    }

    private static VcValidationEvidence NormalizeAndValidate(VcValidationEvidence? evidence, string targetSha)
    {
        if (evidence == null)
            throw new VcInternalException("VALIDATION_INVALID", "Validation evidence must not be null.");
        if (!string.Equals(evidence.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
            throw new VcInternalException("VALIDATION_INVALID", "Validation evidence schemaVersion must be '1.0'.");
        if (evidence.EvidenceKind is not ("tia-sync" or "feature-merge"))
            throw new VcInternalException("VALIDATION_INVALID", "Validation evidence kind must be 'tia-sync' or 'feature-merge'.");
        if (!string.Equals(evidence.CommitSha, targetSha, StringComparison.OrdinalIgnoreCase))
            throw new VcInternalException("VALIDATION_TARGET_MISMATCH", "Validation evidence commitSha does not match its target commit.");
        RequireText(evidence.WorkbenchId, "workbenchId");
        RequireText(evidence.ConfirmedAt, "confirmedAt");
        RequireText(evidence.ConfirmedBy, "confirmedBy");
        if (evidence.Devices == null)
            throw new VcInternalException("VALIDATION_INVALID", "Validation evidence devices must not be null.");

        var devices = evidence.Devices
            .Select(device => NormalizeDevice(device))
            .OrderBy(device => device.DeviceId, StringComparer.Ordinal)
            .ToArray();
        if (devices.Select(device => device.DeviceId).Distinct(StringComparer.Ordinal).Count() != devices.Length)
            throw new VcInternalException("VALIDATION_INVALID", "Validation evidence contains duplicate device IDs.");

        return new VcValidationEvidence(
            SchemaVersion,
            evidence.EvidenceKind,
            targetSha.ToLowerInvariant(),
            evidence.WorkbenchId,
            string.IsNullOrWhiteSpace(evidence.SourceWorktreeId) ? null : evidence.SourceWorktreeId,
            evidence.ConfirmedAt,
            evidence.ConfirmedBy,
            evidence.MachineValidated,
            devices);
    }

    private static VcDeviceValidation NormalizeDevice(VcDeviceValidation device)
    {
        if (device == null)
            throw new VcInternalException("VALIDATION_INVALID", "Validation evidence contains a null device.");
        RequireText(device.DeviceId, "deviceId");
        RequireText(device.PlcName, "plcName");
        RequireText(device.ProjectIdentity, "projectIdentity");
        RequireText(device.ProjectChecksum, "projectChecksum");
        if (device.Objects == null)
            throw new VcInternalException("VALIDATION_INVALID", "Validation device objects must not be null.");

        var objects = device.Objects
            .Select(item =>
            {
                if (item == null)
                    throw new VcInternalException("VALIDATION_INVALID", "Validation evidence contains a null object.");
                RequireText(item.Identity, "object.identity");
                RequireText(item.RelativePath, "object.relativePath");
                RequireText(item.Sha256, "object.sha256");
                return item;
            })
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ThenBy(item => item.Identity, StringComparer.Ordinal)
            .ToArray();

        if (objects.Select(item => item.RelativePath).Distinct(StringComparer.Ordinal).Count() != objects.Length)
            throw new VcInternalException("VALIDATION_INVALID", $"Validation device '{device.DeviceId}' contains duplicate object paths.");

        return new VcDeviceValidation(
            device.DeviceId,
            device.PlcName,
            device.ProjectIdentity,
            device.ProjectChecksum,
            objects);
    }

    private static Commit RequireCommit(Repository repository, string? commitSha)
    {
        if (string.IsNullOrWhiteSpace(commitSha))
            throw new VcInternalException("COMMIT_REQUIRED", "commitSha must not be empty.");

        var commit = repository.Lookup<Commit>(commitSha);
        if (commit == null)
            throw new VcInternalException("REF_NOT_FOUND", $"Commit '{commitSha}' was not found.");
        return commit;
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new VcInternalException("VALIDATION_INVALID", $"Validation evidence {name} must not be empty.");
    }

    private static string? TryReadEvidenceKind(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("evidenceKind", out var property)
                && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ResolveEmail(string confirmedBy)
    {
        var separator = confirmedBy.IndexOf('<', StringComparison.Ordinal);
        return separator > 0 && confirmedBy.EndsWith('>')
            ? confirmedBy[(separator + 1)..^1].Trim()
            : "assistant@plc-assistant.local";
    }
}

internal sealed record ValidationTagRead(
    string State,
    string? EvidenceKind,
    VcValidationEvidence? Evidence);
