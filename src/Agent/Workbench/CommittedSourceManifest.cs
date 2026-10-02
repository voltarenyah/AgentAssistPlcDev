using System.Text.Json;
using Contracts.Engineering;

namespace Agent.Workbench;

/// <summary>
/// Reads one source object's lightweight fingerprint evidence out of the Git-tracked per-device
/// export manifest (<c>devices/&lt;plc&gt;/source/metadata.json</c>). ADR-0003 binds a task-stage
/// baseline to committed source content, so a stage baseline is built from the manifest blob at a
/// Git commit and never from a live TIA capture: a live fingerprint would mark an object the user
/// already changed in TIA but never committed as "in sync" and hide exactly the change the ADR
/// protects.
/// </summary>
/// <remarks>
/// The reader mirrors <see cref="DeviceSnapshotReader.ReadManifestSourceObjects"/> field for field
/// (same stable-ID fallback, same failed-export filter) and the live adapter's evidence mapping
/// (category to <see cref="ManagedSourceEvidenceKind"/>, Instance DBs excluded), because the
/// fingerprint comparison matches baseline and live evidence by that stable ID and kind.
/// </remarks>
public static class CommittedSourceManifest
{
    /// <summary>
    /// Returns the committed fingerprint evidence for <paramref name="sourceObjectId"/>, or null
    /// when the manifest is absent/unparseable, the object is not listed (it has no committed Git
    /// content yet), or the object carries no comparable evidence (an Instance DB, or a component
    /// whose export failed). Null is the correct, intended result there — the caller leaves the
    /// stage baseline null and <c>TASK_STAGE_BASELINE_MISSING</c> reports the real state.
    /// </summary>
    public static ManagedSourceEvidenceObject? TryReadObjectEvidence(string? manifestJson, string sourceObjectId)
    {
        if (string.IsNullOrWhiteSpace(manifestJson) || string.IsNullOrWhiteSpace(sourceObjectId))
        {
            return null;
        }

        try
        {
            using var manifest = JsonDocument.Parse(manifestJson);
            if (manifest.RootElement.ValueKind != JsonValueKind.Object
                || !manifest.RootElement.TryGetProperty("components", out var components)
                || components.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var component in components.EnumerateArray())
            {
                if (component.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var exportedFile = ReadString(component, "exportedFile");
                var name = ReadString(component, "name");
                var category = ReadString(component, "category");
                if (string.IsNullOrWhiteSpace(exportedFile)
                    || string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(category))
                {
                    // Failed exports carry no file and are not comparable objects.
                    continue;
                }

                var relativePath = exportedFile.Replace('\\', '/');
                var id = ReadString(component, "id") ?? $"source:{relativePath}";
                if (!string.Equals(id, sourceObjectId, StringComparison.Ordinal))
                {
                    continue;
                }

                return Build(component, id, name, category, relativePath);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ManagedSourceEvidenceObject? Build(
        JsonElement component,
        string id,
        string name,
        string category,
        string relativePath)
    {
        var siemensTypeName = ReadString(component, "siemensTypeName");
        var kind = category switch
        {
            "Tags" => ManagedSourceEvidenceKind.TagTable,
            "UDT" => ManagedSourceEvidenceKind.Udt,
            _ when string.Equals(siemensTypeName, "InstanceDB", StringComparison.Ordinal) => ManagedSourceEvidenceKind.InstanceDb,
            _ => ManagedSourceEvidenceKind.StandardBlock,
        };
        if (string.Equals(kind, ManagedSourceEvidenceKind.InstanceDb, StringComparison.Ordinal))
        {
            // Instance DBs are excluded from managed-source evidence and comparison decisions.
            return null;
        }

        var fingerprints = ReadFingerprintSet(component, "fingerprints", out var legacyCanonical)
            ?? FingerprintSet.Parse(legacyCanonical);
        var modified = ReadDate(component, "modifiedDate");
        var readable = kind switch
        {
            ManagedSourceEvidenceKind.TagTable => modified is not null,
            ManagedSourceEvidenceKind.StandardBlock or ManagedSourceEvidenceKind.Udt => fingerprints is not null,
            _ => true,
        };
        return new ManagedSourceEvidenceObject
        {
            Id = id,
            Name = name,
            SourcePath = ReadString(component, "sourcePath") ?? relativePath,
            Category = category,
            Kind = kind,
            ReadState = readable ? ManagedSourceEvidenceReadState.Readable : ManagedSourceEvidenceReadState.Unreadable,
            Fingerprints = fingerprints,
            ModifiedTimeStamp = kind == ManagedSourceEvidenceKind.TagTable ? modified : null,
        };
    }

    private static string? ReadString(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadDate(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var date)
            ? date
            : null;

    private static FingerprintSet? ReadFingerprintSet(JsonElement owner, string property, out string? legacyCanonical)
    {
        legacyCanonical = null;
        if (!owner.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            legacyCanonical = value.GetString();
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new FingerprintSet();
        foreach (var item in value.EnumerateObject())
        {
            if (item.Value.ValueKind == JsonValueKind.String && item.Value.GetString() is { } fingerprint)
            {
                result[item.Name] = fingerprint;
            }
        }

        return result.Count == 0 ? null : result;
    }
}
