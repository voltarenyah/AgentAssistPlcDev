namespace Mcp.Engineering.Export;

/// <summary>
/// Pure diff between manifest records and live TIA components (buildnote/plan/export-sync.md).
/// Detection signals per category: blocks/UDTs compare TIA fingerprints (in-memory, no export —
/// fingerprints consider only user input, so saves/compiles never move them); tag tables have no
/// FingerprintProvider, so timestamps nominate and the post-export content hash confirms.
/// No Siemens types — the adapter flattens blocks/tag tables/UDTs into <see cref="SyncLiveComponent"/>
/// first, which makes this the unit-test seam.
/// </summary>
internal enum SyncAction
{
    /// <summary>Record and live item agree — keep the record, no export.</summary>
    Skip,

    /// <summary>Export the live item and rebuild its record (new, changed, or stale record).</summary>
    ReExport,

    /// <summary>No export — stamp the record with the live fingerprints (legacy backfill when
    /// timestamps prove the content stood still).</summary>
    UpdateRecord,

    /// <summary>No live item for this record — delete the XML file and drop the record.</summary>
    Remove,
}

/// <summary>A live TIA object flattened to plain values (id formula shared with the manifest).</summary>
internal sealed class SyncLiveComponent
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Canonical "Id=Value;…" fingerprint string (blocks/UDTs); null for tag tables and
    /// when the provider is missing/throws (e.g. inconsistent block) → timestamp path applies.</summary>
    public string? Fingerprints { get; set; }

    public DateTimeOffset? ModifiedDate { get; set; }
    public DateTimeOffset? CodeModifiedDate { get; set; }
    public DateTimeOffset? InterfaceModifiedDate { get; set; }
}

internal sealed class SyncPlanItem
{
    public SyncAction Action { get; set; }

    /// <summary>Why the planner chose the action (surfaced as SyncChange.Reason).</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Live side; null for <see cref="SyncAction.Remove"/>.</summary>
    public SyncLiveComponent? Live { get; set; }

    /// <summary>Manifest side; null for live items without a record (new components).</summary>
    public ExportMetadataRecord? Record { get; set; }
}

internal static class SyncPlanner
{
    public const string ReasonNew = "new";
    public const string ReasonFingerprint = "fingerprint";
    public const string ReasonTimestamp = "timestamp";
    public const string ReasonFingerprintBackfill = "fingerprint-backfill";
    public const string ReasonLegacyNoHash = "legacy-no-hash";
    public const string ReasonUnreadableMetadata = "unreadable-metadata";
    public const string ReasonPreviousExportFailed = "previous-export-failed";
    public const string ReasonRemovedFromTia = "removed-from-tia";
    public const string ReasonUnchanged = "unchanged";

    public static List<SyncPlanItem> Plan(
        IReadOnlyList<ExportMetadataRecord> records,
        IReadOnlyList<SyncLiveComponent> live)
    {
        var recordsById = new Dictionary<string, ExportMetadataRecord>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            recordsById[record.Id] = record;
        }

        var result = new List<SyncPlanItem>();
        var liveIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in live)
        {
            liveIds.Add(item.Id);
            if (!recordsById.TryGetValue(item.Id, out var record))
            {
                result.Add(new SyncPlanItem { Action = SyncAction.ReExport, Reason = ReasonNew, Live = item });
                continue;
            }

            result.Add(new SyncPlanItem
            {
                Action = ActionFor(record, item, out var reason),
                Reason = reason,
                Live = item,
                Record = record,
            });
        }

        foreach (var record in records)
        {
            if (!liveIds.Contains(record.Id))
            {
                result.Add(new SyncPlanItem { Action = SyncAction.Remove, Reason = ReasonRemovedFromTia, Record = record });
            }
        }

        return result;
    }

    private static SyncAction ActionFor(ExportMetadataRecord record, SyncLiveComponent item, out string reason)
    {
        // A previously failed export leaves a record with no usable file/hash — always retry.
        if (!string.Equals(record.Status, "Exported", StringComparison.OrdinalIgnoreCase) || record.ExportedFile is null)
        {
            reason = ReasonPreviousExportFailed;
            return SyncAction.ReExport;
        }

        if (item.Fingerprints is not null)
        {
            // Fingerprint path (blocks/UDTs): exact per-object change detection, no export needed.
            if (record.Fingerprints is not null)
            {
                if (string.Equals(record.Fingerprints, item.Fingerprints, StringComparison.Ordinal))
                {
                    reason = ReasonUnchanged;
                    return SyncAction.Skip;
                }

                reason = ReasonFingerprint;
                return SyncAction.ReExport;
            }

            // Legacy record (pre-fingerprints): no stored fingerprint to compare. Matching
            // timestamps prove the content stood still → backfill the fingerprint without export.
            if (TimestampsMatch(record, item))
            {
                reason = ReasonFingerprintBackfill;
                return SyncAction.UpdateRecord;
            }

            reason = ReasonTimestamp;
            return SyncAction.ReExport;
        }

        // Timestamp path (tag tables, or unreadable fingerprint provider). Know-how-protected or
        // otherwise unreadable metadata: no cheap signal at all — re-export conservatively; the
        // content hash still decides changed vs touched.
        if (item.ModifiedDate is null && item.CodeModifiedDate is null && item.InterfaceModifiedDate is null)
        {
            reason = ReasonUnreadableMetadata;
            return SyncAction.ReExport;
        }

        if (!TimestampsMatch(record, item))
        {
            reason = ReasonTimestamp;
            return SyncAction.ReExport;
        }

        // Legacy manifests carry no content hash — backfill once (timestamps agree → "touched").
        if (record.ContentHash is null)
        {
            reason = ReasonLegacyNoHash;
            return SyncAction.ReExport;
        }

        reason = ReasonUnchanged;
        return SyncAction.Skip;
    }

    private static bool TimestampsMatch(ExportMetadataRecord record, SyncLiveComponent item) =>
        item.ModifiedDate == record.ModifiedDate
        && item.CodeModifiedDate == record.CodeModifiedDate
        && item.InterfaceModifiedDate == record.InterfaceModifiedDate;
}
