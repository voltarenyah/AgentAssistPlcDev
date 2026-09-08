using Contracts.Engineering;

namespace Mcp.Engineering.Export;

/// <summary>
/// Pure policy for normal fingerprint-first TIA Compare. This is intentionally separate from
/// <see cref="SyncPlanner"/>, whose full-export cache rules include Instance DB re-exporting.
/// </summary>
internal static class SourceEvidencePlanner
{
    public static SourceEvidenceComparisonResult Compare(
        IReadOnlyList<ManagedSourceEvidenceObject> baseline,
        IReadOnlyList<ManagedSourceEvidenceObject> live,
        bool checksumChanged)
    {
        var baselineById = IndexManagedObjects(baseline);
        var liveById = IndexManagedObjects(live);
        var candidates = new List<SourceEvidenceCandidate>();

        foreach (var id in baselineById.Keys.Concat(liveById.Keys).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal))
        {
            baselineById.TryGetValue(id, out var stored);
            liveById.TryGetValue(id, out var current);
            var candidate = CompareObject(stored, current);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return new SourceEvidenceComparisonResult
        {
            Candidates = candidates,
            IsUntrackable = checksumChanged && candidates.Count == 0,
        };
    }

    private static Dictionary<string, ManagedSourceEvidenceObject> IndexManagedObjects(
        IReadOnlyList<ManagedSourceEvidenceObject> source) => source
        .Where(item => !string.Equals(item.Kind, ManagedSourceEvidenceKind.InstanceDb, StringComparison.Ordinal))
        .ToDictionary(item => item.Id, item => item, StringComparer.Ordinal);

    private static SourceEvidenceCandidate? CompareObject(
        ManagedSourceEvidenceObject? baseline,
        ManagedSourceEvidenceObject? live)
    {
        if (baseline is null)
        {
            return Create(live!, SourceEvidenceCandidateReason.New, requiresXmlExport: !IsFBlock(live!), isSafetyDifference: IsFBlock(live!), null, live);
        }

        if (live is null)
        {
            return Create(baseline, SourceEvidenceCandidateReason.Removed, requiresXmlExport: false, isSafetyDifference: IsFBlock(baseline), baseline, null);
        }

        if (!string.Equals(baseline.Kind, live.Kind, StringComparison.Ordinal))
        {
            return Create(live, SourceEvidenceCandidateReason.EvidenceKindChanged, requiresXmlExport: !IsFBlock(live), isSafetyDifference: IsFBlock(live), baseline, live);
        }

        return live.Kind switch
        {
            ManagedSourceEvidenceKind.StandardBlock or ManagedSourceEvidenceKind.Udt => CompareFingerprint(baseline, live),
            ManagedSourceEvidenceKind.TagTable => CompareTagTimestamp(baseline, live),
            ManagedSourceEvidenceKind.FBlock => CompareFSignature(baseline, live),
            _ => Create(live, SourceEvidenceCandidateReason.EvidenceUnreadable, requiresXmlExport: true, isSafetyDifference: false, baseline, live),
        };
    }

    private static SourceEvidenceCandidate? CompareFingerprint(ManagedSourceEvidenceObject baseline, ManagedSourceEvidenceObject live)
    {
        if (baseline.Fingerprints is null || live.Fingerprints is null)
        {
            return Create(live, SourceEvidenceCandidateReason.EvidenceUnreadable, requiresXmlExport: true, isSafetyDifference: false, baseline, live);
        }

        return string.Equals(baseline.Fingerprints.ToCanonicalString(), live.Fingerprints.ToCanonicalString(), StringComparison.Ordinal)
            ? null
            : Create(live, SourceEvidenceCandidateReason.FingerprintChanged, requiresXmlExport: true, isSafetyDifference: false, baseline, live);
    }

    private static SourceEvidenceCandidate? CompareTagTimestamp(ManagedSourceEvidenceObject baseline, ManagedSourceEvidenceObject live)
    {
        if (baseline.ModifiedTimeStamp is null || live.ModifiedTimeStamp is null)
        {
            return Create(live, SourceEvidenceCandidateReason.EvidenceUnreadable, requiresXmlExport: true, isSafetyDifference: false, baseline, live);
        }

        return baseline.ModifiedTimeStamp == live.ModifiedTimeStamp
            ? null
            : Create(live, SourceEvidenceCandidateReason.TagTimestampChanged, requiresXmlExport: true, isSafetyDifference: false, baseline, live);
    }

    private static SourceEvidenceCandidate? CompareFSignature(ManagedSourceEvidenceObject baseline, ManagedSourceEvidenceObject live)
    {
        if (baseline.FSignature is null || live.FSignature is null)
        {
            return Create(live, SourceEvidenceCandidateReason.EvidenceUnreadable, requiresXmlExport: false, isSafetyDifference: true, baseline, live);
        }

        if (string.Equals(baseline.FSignature, live.FSignature, StringComparison.Ordinal))
        {
            return null;
        }

        return Create(live, SourceEvidenceCandidateReason.FSignatureChanged,
            requiresXmlExport: false, isSafetyDifference: true, baseline, live);
    }

    private static bool IsFBlock(ManagedSourceEvidenceObject evidence) =>
        string.Equals(evidence.Kind, ManagedSourceEvidenceKind.FBlock, StringComparison.Ordinal);

    private static SourceEvidenceCandidate Create(
        ManagedSourceEvidenceObject subject,
        string reason,
        bool requiresXmlExport,
        bool isSafetyDifference,
        ManagedSourceEvidenceObject? baseline,
        ManagedSourceEvidenceObject? live) => new()
    {
        Id = subject.Id,
        Reason = reason,
        RequiresXmlExport = requiresXmlExport,
        IsSafetyDifference = isSafetyDifference,
        Baseline = baseline,
        Live = live,
    };
}
