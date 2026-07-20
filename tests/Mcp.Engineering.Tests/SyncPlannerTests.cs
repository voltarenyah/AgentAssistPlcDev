using System;
using System.Collections.Generic;
using System.Linq;
using Mcp.Engineering.Export;
using Xunit;

namespace Mcp.Engineering.Tests;

/// <summary>
/// Diff matrix for <see cref="SyncPlanner"/> (buildnote/plan/export-sync.md): fingerprint path for
/// blocks/UDTs, timestamp+hash path for tag tables, legacy-manifest migration, removals.
/// </summary>
public sealed class SyncPlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 18, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 7, 19, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewLiveItem_IsReExportedAsNew()
    {
        var plan = SyncPlanner.Plan(
            Array.Empty<ExportMetadataRecord>(),
            new[] { Live(fingerprints: "fp1") });

        var item = Assert.Single(plan);
        Assert.Equal(SyncAction.ReExport, item.Action);
        Assert.Equal(SyncPlanner.ReasonNew, item.Reason);
        Assert.Null(item.Record);
    }

    [Fact]
    public void MatchingFingerprints_Skip()
    {
        var plan = SyncPlanner.Plan(
            new[] { Record(fingerprints: "fp1") },
            new[] { Live(fingerprints: "fp1") });

        var item = Assert.Single(plan);
        Assert.Equal(SyncAction.Skip, item.Action);
        Assert.Equal(SyncPlanner.ReasonUnchanged, item.Reason);
    }

    [Fact]
    public void DifferentFingerprints_ReExport()
    {
        var plan = SyncPlanner.Plan(
            new[] { Record(fingerprints: "fp1") },
            new[] { Live(fingerprints: "fp2") });

        var item = Assert.Single(plan);
        Assert.Equal(SyncAction.ReExport, item.Action);
        Assert.Equal(SyncPlanner.ReasonFingerprint, item.Reason);
    }

    [Fact]
    public void LegacyRecord_MatchingTimestamps_BackfillsFingerprintWithoutExport()
    {
        var plan = SyncPlanner.Plan(
            new[] { Record(fingerprints: null) },
            new[] { Live(fingerprints: "fp1") });

        var item = Assert.Single(plan);
        Assert.Equal(SyncAction.UpdateRecord, item.Action);
        Assert.Equal(SyncPlanner.ReasonFingerprintBackfill, item.Reason);
    }

    [Fact]
    public void LegacyRecord_MovedTimestamps_ReExports()
    {
        var plan = SyncPlanner.Plan(
            new[] { Record(fingerprints: null) },
            new[] { Live(fingerprints: "fp1", modified: T1) });

        var item = Assert.Single(plan);
        Assert.Equal(SyncAction.ReExport, item.Action);
        Assert.Equal(SyncPlanner.ReasonTimestamp, item.Reason);
    }

    [Fact]
    public void TagTable_MatchingTimestampsAndHash_Skip()
    {
        var plan = SyncPlanner.Plan(
            new[] { Record(fingerprints: null, contentHash: "hash1") },
            new[] { Live(fingerprints: null) });

        var item = Assert.Single(plan);
        Assert.Equal(SyncAction.Skip, item.Action);
    }

    [Fact]
    public void TagTable_MovedTimestamp_ReExport()
    {
        var plan = SyncPlanner.Plan(
            new[] { Record(fingerprints: null, contentHash: "hash1") },
            new[] { Live(fingerprints: null, modified: T1) });

        var item = Assert.Single(plan);
        Assert.Equal(SyncAction.ReExport, item.Action);
        Assert.Equal(SyncPlanner.ReasonTimestamp, item.Reason);
    }

    [Fact]
    public void TagTable_LegacyNoHash_BackfillsHashOnce()
    {
        var plan = SyncPlanner.Plan(
            new[] { Record(fingerprints: null, contentHash: null) },
            new[] { Live(fingerprints: null) });

        var item = Assert.Single(plan);
        Assert.Equal(SyncAction.ReExport, item.Action);
        Assert.Equal(SyncPlanner.ReasonLegacyNoHash, item.Reason);
    }

    [Fact]
    public void UnreadableMetadata_ReExportConservatively()
    {
        var plan = SyncPlanner.Plan(
            new[] { Record(fingerprints: null, contentHash: "hash1") },
            new[] { Live(fingerprints: null, nullTimestamps: true) });

        var item = Assert.Single(plan);
        Assert.Equal(SyncAction.ReExport, item.Action);
        Assert.Equal(SyncPlanner.ReasonUnreadableMetadata, item.Reason);
    }

    [Fact]
    public void FailedRecord_IsRetried()
    {
        var plan = SyncPlanner.Plan(
            new[] { Record(status: "Failed", exportedFile: null, fingerprints: "fp1") },
            new[] { Live(fingerprints: "fp1") });

        var item = Assert.Single(plan);
        Assert.Equal(SyncAction.ReExport, item.Action);
        Assert.Equal(SyncPlanner.ReasonPreviousExportFailed, item.Reason);
    }

    [Fact]
    public void RecordWithoutLiveItem_IsRemoved()
    {
        var plan = SyncPlanner.Plan(
            new[] { Record(fingerprints: "fp1") },
            Array.Empty<SyncLiveComponent>());

        var item = Assert.Single(plan);
        Assert.Equal(SyncAction.Remove, item.Action);
        Assert.Equal(SyncPlanner.ReasonRemovedFromTia, item.Reason);
        Assert.Null(item.Live);
    }

    [Fact]
    public void MixedPlan_CoversAllBuckets()
    {
        var records = new[]
        {
            Record(id: "keep", fingerprints: "fp1"),
            Record(id: "change", fingerprints: "fp1"),
            Record(id: "gone", fingerprints: "fp1"),
        };
        var live = new[]
        {
            Live(id: "keep", fingerprints: "fp1"),
            Live(id: "change", fingerprints: "fp2"),
            Live(id: "brand-new", fingerprints: "fp3"),
        };

        var plan = SyncPlanner.Plan(records, live);

        Assert.Equal(4, plan.Count);
        Assert.Single(plan, item => item.Action == SyncAction.Skip && item.Live!.Id == "keep");
        Assert.Single(plan, item => item.Action == SyncAction.ReExport && item.Reason == SyncPlanner.ReasonFingerprint && item.Live!.Id == "change");
        Assert.Single(plan, item => item.Action == SyncAction.ReExport && item.Reason == SyncPlanner.ReasonNew && item.Live!.Id == "brand-new");
        Assert.Single(plan, item => item.Action == SyncAction.Remove && item.Record!.Id == "gone");
    }

    private static ExportMetadataRecord Record(
        string id = "id1",
        string status = "Exported",
        string? exportedFile = @"Blocks\A [FB1].xml",
        string? contentHash = "hash1",
        string? fingerprints = "fp1",
        DateTimeOffset? modified = null,
        DateTimeOffset? codeModified = null,
        DateTimeOffset? interfaceModified = null) => new()
        {
            Id = id,
            Name = "A",
            SourcePath = "A",
            Category = "FB",
            Status = status,
            ExportedFile = exportedFile,
            ContentHash = contentHash,
            Fingerprints = fingerprints,
            ModifiedDate = modified ?? T0,
            CodeModifiedDate = codeModified ?? T0,
            InterfaceModifiedDate = interfaceModified ?? T0,
        };

    private static SyncLiveComponent Live(
        string id = "id1",
        string? fingerprints = "fp1",
        DateTimeOffset? modified = null,
        DateTimeOffset? codeModified = null,
        DateTimeOffset? interfaceModified = null,
        bool nullTimestamps = false) => new()
        {
            Id = id,
            Name = "A",
            Category = "FB",
            SourcePath = "A",
            Fingerprints = fingerprints,
            ModifiedDate = nullTimestamps ? null : modified ?? T0,
            CodeModifiedDate = nullTimestamps ? null : codeModified ?? T0,
            InterfaceModifiedDate = nullTimestamps ? null : interfaceModified ?? T0,
        };
}
