using System;
using Contracts.Engineering;
using Mcp.Engineering.Export;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class SourceEvidencePlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddMinutes(1);

    [Fact]
    public void EqualBlockAndUdtFingerprints_AreNotXmlCandidates()
    {
        var result = SourceEvidencePlanner.Compare(
            new[] { Block("block", "Code=a;Interface=b"), Udt("udt", "Layout=x") },
            new[] { Block("block", "Code=a;Interface=b"), Udt("udt", "Layout=x") },
            checksumChanged: false);

        Assert.Empty(result.XmlCandidates);
        Assert.Empty(result.SafetyDifferences);
        Assert.False(result.IsUntrackable);
    }

    [Fact]
    public void ChangedFingerprint_NominatesExactlyThatObjectForXml()
    {
        var result = SourceEvidencePlanner.Compare(
            new[] { Block("block", "Code=old;Interface=same"), Udt("udt", "Layout=same") },
            new[] { Block("block", "Code=new;Interface=same"), Udt("udt", "Layout=same") },
            checksumChanged: true);

        var candidate = Assert.Single(result.XmlCandidates);
        Assert.Equal("block", candidate.Id);
        Assert.Equal(SourceEvidenceCandidateReason.FingerprintChanged, candidate.Reason);
        Assert.False(result.IsUntrackable);
    }

    [Fact]
    public void EqualTagTimestamp_IsNotXmlCandidate_ButUnreadableTagIs()
    {
        var equal = SourceEvidencePlanner.Compare(
            new[] { Tag("tag", T0) },
            new[] { Tag("tag", T0) },
            checksumChanged: false);
        Assert.Empty(equal.XmlCandidates);

        var unreadable = SourceEvidencePlanner.Compare(
            new[] { Tag("tag", T0) },
            new[] { Tag("tag", null) },
            checksumChanged: false);
        var candidate = Assert.Single(unreadable.XmlCandidates);
        Assert.Equal(SourceEvidenceCandidateReason.EvidenceUnreadable, candidate.Reason);
    }

    [Fact]
    public void EqualFSignature_SkipsXml_ButAnyDifferentSignatureIncludingZeroIsSafetyOnly()
    {
        var changed = SourceEvidencePlanner.Compare(
            new[] { FBlock("f", "12345678") },
            new[] { FBlock("f", "87654321") },
            checksumChanged: true);
        var changedSafety = Assert.Single(changed.SafetyDifferences);
        Assert.Equal(SourceEvidenceCandidateReason.FSignatureChanged, changedSafety.Reason);
        Assert.Empty(changed.XmlCandidates);

        var zeroValue = SourceEvidencePlanner.Compare(
            new[] { FBlock("f", "12345678") },
            new[] { FBlock("f", "00000000") },
            checksumChanged: true);
        var zeroValueSafety = Assert.Single(zeroValue.SafetyDifferences);
        Assert.Equal(SourceEvidenceCandidateReason.FSignatureChanged, zeroValueSafety.Reason);
        Assert.Empty(zeroValue.XmlCandidates);
    }

    [Fact]
    public void InstanceDb_IsExcludedFromNewChangedAndRemovedAccounting()
    {
        var result = SourceEvidencePlanner.Compare(
            new[] { InstanceDb("old") },
            new[] { InstanceDb("new") },
            checksumChanged: true);

        Assert.Empty(result.XmlCandidates);
        Assert.Empty(result.SafetyDifferences);
        Assert.True(result.IsUntrackable);
    }

    [Fact]
    public void ChecksumChangeWithoutManagedEvidenceDifference_IsUntrackable()
    {
        var result = SourceEvidencePlanner.Compare(
            new[] { Block("block", "Code=same"), Tag("tag", T0), FBlock("f", "12345678") },
            new[] { Block("block", "Code=same"), Tag("tag", T0), FBlock("f", "12345678") },
            checksumChanged: true);

        Assert.True(result.IsUntrackable);
        Assert.Empty(result.XmlCandidates);
        Assert.Empty(result.SafetyDifferences);
    }

    private static ManagedSourceEvidenceObject Block(string id, string? fingerprint) => new()
    {
        Id = id,
        Name = id,
        SourcePath = id,
        Category = "FB",
        Kind = ManagedSourceEvidenceKind.StandardBlock,
        Fingerprints = FingerprintSet.Parse(fingerprint),
    };

    private static ManagedSourceEvidenceObject Udt(string id, string? fingerprint) => new()
    {
        Id = id,
        Name = id,
        SourcePath = id,
        Category = "UDT",
        Kind = ManagedSourceEvidenceKind.Udt,
        Fingerprints = FingerprintSet.Parse(fingerprint),
    };

    private static ManagedSourceEvidenceObject Tag(string id, DateTimeOffset? timestamp) => new()
    {
        Id = id,
        Name = id,
        SourcePath = id,
        Category = "Tags",
        Kind = ManagedSourceEvidenceKind.TagTable,
        ModifiedTimeStamp = timestamp,
    };

    private static ManagedSourceEvidenceObject FBlock(string id, string? signature) => new()
    {
        Id = id,
        Name = id,
        SourcePath = id,
        Category = "FB",
        Kind = ManagedSourceEvidenceKind.FBlock,
        FSignature = signature,
    };

    private static ManagedSourceEvidenceObject InstanceDb(string id) => new()
    {
        Id = id,
        Name = id,
        SourcePath = id,
        Category = "DB",
        Kind = ManagedSourceEvidenceKind.InstanceDb,
    };
}
