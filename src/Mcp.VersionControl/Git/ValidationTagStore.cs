using System.Text.Json;
using System.Text.Json.Serialization;
using Contracts.Engineering;
using LibGit2Sharp;

namespace Mcp.VersionControl.Git;

/// <summary>Immutable, app-owned annotated Git tags containing TIA validation evidence.</summary>
internal static class ValidationTagStore
{
    public const string TagPrefix = "tia-validation/";
    public const string SchemaVersion = SchemaVersionV1;
    public const string SchemaVersionV1 = "1.0";
    public const string SchemaVersionV2 = "2.0";
    public const string EvidenceKindManagedSource = "tia-managed-source";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Fingerprint component IDs are case-sensitive Siemens names (for example "Code" and
        // "Interface"); camel-casing dictionary keys would change their evidence identity.
        DictionaryKeyPolicy = null,
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
        var isV1 = string.Equals(evidence.SchemaVersion, SchemaVersionV1, StringComparison.Ordinal);
        var isV2 = string.Equals(evidence.SchemaVersion, SchemaVersionV2, StringComparison.Ordinal);
        if (!isV1 && !isV2)
            throw new VcInternalException("VALIDATION_INVALID", "Validation evidence schemaVersion must be '1.0' or '2.0'.");
        if (isV1 && evidence.EvidenceKind is not ("tia-sync" or "feature-merge"))
            throw new VcInternalException("VALIDATION_INVALID", "Schema v1 validation evidence kind must be 'tia-sync' or 'feature-merge'.");
        if (isV2 && !string.Equals(evidence.EvidenceKind, EvidenceKindManagedSource, StringComparison.Ordinal))
            throw new VcInternalException("VALIDATION_INVALID", "Schema v2 validation evidence kind must be 'tia-managed-source'.");
        if (isV2 && evidence.ManagedSourceConsistent is null)
            throw new VcInternalException("VALIDATION_INVALID", "Schema v2 validation evidence must declare managedSourceConsistent.");
        if (!string.Equals(evidence.CommitSha, targetSha, StringComparison.OrdinalIgnoreCase))
            throw new VcInternalException("VALIDATION_TARGET_MISMATCH", "Validation evidence commitSha does not match its target commit.");
        RequireText(evidence.WorkbenchId, "workbenchId");
        RequireText(evidence.ConfirmedAt, "confirmedAt");
        RequireText(evidence.ConfirmedBy, "confirmedBy");
        if (evidence.Devices == null)
            throw new VcInternalException("VALIDATION_INVALID", "Validation evidence devices must not be null.");

        var devices = evidence.Devices
            .Select(device => NormalizeDevice(device, requireSourceEvidence: isV2))
            .OrderBy(device => device.DeviceId, StringComparer.Ordinal)
            .ToArray();
        if (devices.Select(device => device.DeviceId).Distinct(StringComparer.Ordinal).Count() != devices.Length)
            throw new VcInternalException("VALIDATION_INVALID", "Validation evidence contains duplicate device IDs.");

        return new VcValidationEvidence(
            evidence.SchemaVersion,
            evidence.EvidenceKind,
            targetSha.ToLowerInvariant(),
            evidence.WorkbenchId,
            string.IsNullOrWhiteSpace(evidence.SourceWorktreeId) ? null : evidence.SourceWorktreeId,
            evidence.ConfirmedAt,
            evidence.ConfirmedBy,
            evidence.MachineValidated,
            devices)
        {
            ManagedSourceConsistent = isV2 ? evidence.ManagedSourceConsistent : null,
        };
    }

    private static VcDeviceValidation NormalizeDevice(VcDeviceValidation device, bool requireSourceEvidence)
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

        var sourceEvidence = requireSourceEvidence
            ? NormalizeSourceEvidence(device)
            : null;

        return new VcDeviceValidation(
            device.DeviceId,
            device.PlcName,
            device.ProjectIdentity,
            device.ProjectChecksum,
            objects)
        {
            SourceEvidence = sourceEvidence,
        };
    }

    private static IReadOnlyList<ManagedSourceEvidenceObject> NormalizeSourceEvidence(VcDeviceValidation device)
    {
        if (device.SourceEvidence is null)
            throw new VcInternalException("VALIDATION_INVALID", $"Schema v2 validation device '{device.DeviceId}' must contain sourceEvidence.");

        var normalized = device.SourceEvidence
            .Select(item => NormalizeSourceEvidenceObject(item))
            .OrderBy(item => item.SourcePath, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new VcInternalException("VALIDATION_INVALID", $"Validation device '{device.DeviceId}' contains duplicate source evidence IDs.");
        if (normalized.Select(item => item.SourcePath).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
            throw new VcInternalException("VALIDATION_INVALID", $"Validation device '{device.DeviceId}' contains duplicate source evidence paths.");
        return normalized;
    }

    private static ManagedSourceEvidenceObject NormalizeSourceEvidenceObject(ManagedSourceEvidenceObject item)
    {
        if (item is null)
            throw new VcInternalException("VALIDATION_INVALID", "Validation sourceEvidence contains a null object.");
        RequireText(item.Id, "sourceEvidence.id");
        RequireText(item.Name, "sourceEvidence.name");
        RequireText(item.SourcePath, "sourceEvidence.sourcePath");
        RequireText(item.Category, "sourceEvidence.category");
        RequireText(item.Kind, "sourceEvidence.kind");
        RequireText(item.ReadState, "sourceEvidence.readState");
        if (string.Equals(item.Kind, ManagedSourceEvidenceKind.InstanceDb, StringComparison.Ordinal))
            throw new VcInternalException("VALIDATION_INVALID", "Instance DBs are excluded from schema v2 sourceEvidence.");
        if (item.Kind is not (ManagedSourceEvidenceKind.StandardBlock or ManagedSourceEvidenceKind.Udt or ManagedSourceEvidenceKind.TagTable or ManagedSourceEvidenceKind.FBlock))
            throw new VcInternalException("VALIDATION_INVALID", $"Unknown source evidence kind '{item.Kind}'.");
        if (item.ReadState is not (ManagedSourceEvidenceReadState.Readable or ManagedSourceEvidenceReadState.Unreadable))
            throw new VcInternalException("VALIDATION_INVALID", $"Unknown source evidence readState '{item.ReadState}'.");

        var fingerprints = item.Fingerprints is null
            ? null
            : new FingerprintSet(item.Fingerprints.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        if (string.Equals(item.ReadState, ManagedSourceEvidenceReadState.Readable, StringComparison.Ordinal))
        {
            if (item.Kind is ManagedSourceEvidenceKind.StandardBlock or ManagedSourceEvidenceKind.Udt && fingerprints is null)
                throw new VcInternalException("VALIDATION_INVALID", "Readable block/UDT source evidence requires fingerprints.");
            if (string.Equals(item.Kind, ManagedSourceEvidenceKind.TagTable, StringComparison.Ordinal) && item.ModifiedTimeStamp is null)
                throw new VcInternalException("VALIDATION_INVALID", "Readable tag-table source evidence requires modifiedTimeStamp.");
            if (string.Equals(item.Kind, ManagedSourceEvidenceKind.FBlock, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(item.FSignature))
                throw new VcInternalException("VALIDATION_INVALID", "Readable F-block source evidence requires fSignature.");
        }

        return new ManagedSourceEvidenceObject
        {
            Id = item.Id,
            Name = item.Name,
            SourcePath = item.SourcePath,
            Category = item.Category,
            Kind = item.Kind,
            ReadState = item.ReadState,
            Fingerprints = fingerprints,
            ModifiedTimeStamp = item.ModifiedTimeStamp,
            FSignature = item.FSignature,
        };
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
