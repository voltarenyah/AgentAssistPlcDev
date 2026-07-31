namespace Mcp.Engineering.Export;

/// <summary>
/// Pure diff between manifest records and live TIA components (buildnote/plan/export-sync.md).
/// Nomination per category: blocks/UDTs compare TIA fingerprints AND timestamps (either may lag
/// the actual edit until TIA propagates it on save/compile — verified 2026-07-21 — so both are
/// checked); a fingerprint match with a moved timestamp still skips the re-export when the caller
/// verifies the exported file on disk is untouched since the last export (fingerprint-verified —
/// compile ripples and timestamp drift no longer nominate). The post-export content hash is always
/// the verdict (fingerprint- or hash-proven changes land in "changed", compile ripples degrade to
/// "touched"). Tag tables have no FingerprintProvider and run the timestamp+hash path by default;
/// instance DBs may get no moved signal at all in the propagation window (system-side
/// regeneration) and are re-exported on every diff for a hash verdict. No Siemens types — the
/// adapter flattens blocks/tag tables/UDTs into <see cref="SyncLiveComponent"/> first, which makes
/// this the unit-test seam.
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

    /// <summary>Concrete Openness type name (e.g. GlobalDB, InstanceDB); used by the instance-DB rule.</summary>
    public string? SiemensTypeName { get; set; }

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
    public const string ReasonInstanceDbVerify = "instance-db-verify";
    public const string ReasonFingerprintBackfill = "fingerprint-backfill";
    public const string ReasonLegacyNoHash = "legacy-no-hash";
    public const string ReasonUnreadableMetadata = "unreadable-metadata";
    public const string ReasonPreviousExportFailed = "previous-export-failed";
    public const string ReasonRemovedFromTia = "removed-from-tia";
    public const string ReasonUnchanged = "unchanged";
    public const string ReasonFingerprintVerified = "fingerprint-verified";

    /// <param name="verifiedLocalFiles">Ids of manifest records whose exported file on disk still
    /// hashes to the recorded content hash — proof the local export was not modified in place since
    /// the last export (local edits belong in the modified-source overlay, never in the export
    /// folder itself). When null, the fingerprint path keeps the conservative timestamp behavior.</param>
    public static List<SyncPlanItem> Plan(
        IReadOnlyList<ExportMetadataRecord> records,
        IReadOnlyList<SyncLiveComponent> live,
        ISet<string>? verifiedLocalFiles = null)
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
                Action = ActionFor(record, item, verifiedLocalFiles, out var reason),
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

    private static SyncAction ActionFor(
        ExportMetadataRecord record,
        SyncLiveComponent item,
        ISet<string>? verifiedLocalFiles,
        out string reason)
    {
        // A previously failed export leaves a record with no usable file/hash — always retry.
        if (!string.Equals(record.Status, "Exported", StringComparison.OrdinalIgnoreCase) || record.ExportedFile is null)
        {
            reason = ReasonPreviousExportFailed;
            return SyncAction.ReExport;
        }

        // Instance DBs can change without any moved per-object signal (verified 2026-07-21):
        // when the parent FB's static area changes, TIA regenerates them system-side and — until
        // the change propagates — neither fingerprints nor modified dates move. Re-export on
        // every diff; the content hash decides changed vs touched.
        if (string.Equals(item.SiemensTypeName, "InstanceDB", StringComparison.Ordinal))
        {
            reason = ReasonInstanceDbVerify;
            return SyncAction.ReExport;
        }

        if (item.Fingerprints is not null)
        {
            // Fingerprint path (blocks/UDTs).
            if (record.Fingerprints is not null)
            {
                if (!string.Equals(record.Fingerprints, item.Fingerprints, StringComparison.Ordinal))
                {
                    reason = ReasonFingerprint;
                    return SyncAction.ReExport;
                }

                // Signals can lag the actual edit until TIA propagates it (save/compile): a moved
                // timestamp nominates even when fingerprints still match (verified for a
                // start-value edit, 2026-07-21). The content hash decides — compile ripples that
                // bump timestamps without content change degrade to "touched".
                if (!TimestampsMatch(record, item))
                {
                    // Fingerprints track user input only, so a match already proves the TIA-side
                    // content stood still. Timestamps also move on save/compile ripples — and can
                    // drift systematically (e.g. precision changes after a project reopen) — which
                    // otherwise makes every diff report "different" forever. When the exported
                    // file on disk is verified untouched since the last export (local edits live
                    // in the modified-source overlay, never in place), there is no remaining
                    // change source: treat as same without a re-export.
                    if (verifiedLocalFiles is not null && verifiedLocalFiles.Contains(record.Id))
                    {
                        reason = ReasonFingerprintVerified;
                        return SyncAction.Skip;
                    }

                    reason = ReasonTimestamp;
                    return SyncAction.ReExport;
                }

                reason = ReasonUnchanged;
                return SyncAction.Skip;
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
