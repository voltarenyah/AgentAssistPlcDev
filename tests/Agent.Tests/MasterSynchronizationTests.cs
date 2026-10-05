using System.Linq;
using Agent.Mcp;
using Agent.Workbench;
using Contracts.Knowledge;
using Contracts.Sandbox;
using Xunit;

namespace Agent.Tests;

public sealed class MasterSynchronizationTests : IDisposable
{
    private readonly SyncFixture fixture = SyncFixture.Create();

    [Fact]
    public async Task ApplyRejectsAComparisonBoundToAnotherWorktree()
    {
        var comparisonPath = System.IO.Path.Combine(fixture.Root, ".automation", "comparisons", "comparison-1.json");
        var comparison = fixture.Store.Read<WorkbenchConsistencyResult>(comparisonPath) with { ComparedWorktreeId = "feature-1" };
        fixture.Store.Write(comparisonPath, comparison);
        var coordinator = fixture.CreateCoordinator();

        var exception = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() => coordinator.ApplyTiaSynchronizationAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            fixture.ComparisonId,
            [fixture.Path("Blocks/A.xml")],
            "Reject cross-worktree apply",
            CancellationToken.None));

        Assert.Equal("COMPARISON_WORKTREE_MISMATCH", exception.Code);
        Assert.DoesNotContain("vc_commit_selected", fixture.VersionControl.Calls);
    }

    [Fact]
    public async Task ApplyCopiesSelectedChangedObjectsAndAutoCommitsWithTheProvidedTitle()
    {
        var coordinator = fixture.CreateCoordinator();

        var result = await coordinator.ApplyTiaSynchronizationAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            fixture.ComparisonId,
            [fixture.Path("Blocks/A.xml")],
            "Accept Main block from TIA",
            CancellationToken.None);

        Assert.Empty(result.PendingPaths);
        Assert.Equal("head-2", result.CommitSha);
        Assert.Equal("new A", File.ReadAllText(fixture.MasterSource("Blocks/A.xml")));
        Assert.Equal("old B", File.ReadAllText(fixture.MasterSource("Blocks/B.xml")));
        Assert.Contains("vc_commit_selected", fixture.VersionControl.Calls);
        Assert.Equal(
            "Accept Main block from TIA",
            fixture.VersionControl.CommitMessage);
    }

    [Fact]
    public async Task ApplyCommitsSelectedTiaSourceIntoTheComparedFeatureWorktree()
    {
        var feature = new WorktreeMetadata(
            "1.0", "feature-1", "wb-1", "debug/cpu", "debug", "now", "feature-head-1", "project-1", SyncFixture.ProjectPath,
            new[] { "device-1" }, null);
        var featureWorkbench = fixture.Workbench with
        {
            Worktrees = new[]
            {
                fixture.Workbench.Worktrees.Single(),
                new WorkbenchWorktreeRegistration("feature-1", "debug/cpu", "debug", "debug/cpu"),
            },
        };
        fixture.Store.Write(System.IO.Path.Combine(fixture.Root, "workbench.json"), featureWorkbench);
        var featureRoot = System.IO.Path.Combine(fixture.Root, "worktrees", "debug", "cpu");
        fixture.Store.Write(System.IO.Path.Combine(featureRoot, "worktree.json"), feature);
        var context = WorkbenchPaths.ResolveDevice("wb-1", fixture.Root, "feature-1", "debug/cpu", "device-1", "PLC_1");
        Directory.CreateDirectory(System.IO.Path.Combine(context.SourceRoot, "Blocks"));
        Directory.CreateDirectory(System.IO.Path.Combine(context.StagingRoot, "Blocks"));
        File.WriteAllText(System.IO.Path.Combine(context.SourceRoot, "Blocks", "A.xml"), "feature old A");
        File.WriteAllText(System.IO.Path.Combine(context.SourceRoot, "metadata.json"), """
            { "components": [ { "id": "a", "name": "A", "category": "FC", "exportedFile": "Blocks/A.xml" } ] }
            """);
        fixture.Store.Write(System.IO.Path.Combine(context.DeviceRoot, "device.json"), new DeviceMetadata(
            "1.0", "device-1", "feature-1", "PLC_1", "project-1", null, null, null,
            new KnowledgeState(false, new Dictionary<string, string>(), null), Array.Empty<DeviceImportRecord>()));
        var comparisonPath = System.IO.Path.Combine(fixture.Root, ".automation", "comparisons", "comparison-1.json");
        fixture.Store.Write(comparisonPath, fixture.Store.Read<WorkbenchConsistencyResult>(comparisonPath) with { ComparedWorktreeId = "feature-1" });
        // TIA holds the content this scenario's exporter hands back.
        fixture.SetLiveDifferenceFingerprint("Blocks/A.xml", "feature fresh A");
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new Contracts.Engineering.ProjectInfo
            {
                Name = "Line",
                Path = SyncFixture.ProjectPath,
                PlcDevices = ["PLC_1"],
            })
            .Respond("export_source_object", args =>
            {
                var outputDir = (string)args.GetType().GetProperty("outputDir")!.GetValue(args)!;
                var exported = System.IO.Path.Combine(outputDir, "Blocks", "A.xml");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(exported)!);
                File.WriteAllText(exported, "feature fresh A");
                return new Contracts.Engineering.ExportResult { Success = true, Path = exported };
            });
        var coordinator = fixture.CreateCoordinator(engineering);

        var result = await coordinator.ApplyTiaSynchronizationAsync(
            fixture.Workbench.WorkbenchId,
            "feature-1",
            fixture.ComparisonId,
            [fixture.Path("Blocks/A.xml")],
            "Accept feature TIA change",
            CancellationToken.None);

        Assert.Equal("head-2", result.CommitSha);
        Assert.Equal("feature fresh A", File.ReadAllText(System.IO.Path.Combine(context.SourceRoot, "Blocks", "A.xml")));
        Assert.Equal("old A", File.ReadAllText(fixture.MasterSource("Blocks/A.xml")));
        Assert.Contains("export_source_object", engineering.Calls);
        Assert.Equal(featureRoot, Property<string>(fixture.VersionControl.CommitArgs!, "repoPath"));
        Assert.Equal("Accept feature TIA change", fixture.VersionControl.CommitMessage);
    }

    [Fact]
    public async Task ApplyRefreshesCanonicalStagingWhenComparisonExportWasTemporary()
    {
        // Project-level compare exports nominated XML into a temporary capture directory. If
        // the persistent staging file still matches master, refresh it from TIA before copying;
        // otherwise the selected block produces an empty Git commit.
        File.WriteAllText(fixture.StagingSource("Blocks/A.xml"), "old A");
        fixture.SetLiveDifferenceFingerprint("Blocks/A.xml", "fresh A");
        File.WriteAllText(fixture.MasterSource("metadata.json"), """
            { "components": [
                { "id": "a", "name": "A", "category": "FC", "exportedFile": "Blocks/A.xml" }
            ] }
            """);
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new Contracts.Engineering.ProjectInfo
            {
                Name = "Line",
                Path = SyncFixture.ProjectPath,
                PlcDevices = ["PLC_1"],
            })
            .Respond("export_source_object", args =>
            {
                var outputDir = (string)args.GetType().GetProperty("outputDir")!.GetValue(args)!;
                var exported = System.IO.Path.Combine(outputDir, "Blocks", "A.xml");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(exported)!);
                File.WriteAllText(exported, "fresh A");
                return new Contracts.Engineering.ExportResult { Success = true, Path = exported };
            });
        var coordinator = fixture.CreateCoordinator(engineering);

        await coordinator.ApplyTiaSynchronizationAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            fixture.ComparisonId,
            [fixture.Path("Blocks/A.xml")],
            "Accept refreshed A",
            CancellationToken.None);

        Assert.Equal("fresh A", File.ReadAllText(fixture.MasterSource("Blocks/A.xml")));
        Assert.Contains("export_source_object", engineering.Calls);
    }

    [Fact]
    public async Task ApplyReExportsFromTiaWhenTheCanonicalStagingCopyIsAStaleExport()
    {
        // Issue #111: staging holds an older export of the same logic. Its file bytes differ from
        // master only by the export timestamp, so a byte comparison against master reads it as
        // this comparison's candidate and commits pre-edit content. The accept path must instead
        // materialize the content the comparison observed in TIA.
        const string baselineExport = """
            <Document>
              <Created>2026-10-03T11:22:59Z</Created>
              <Logic>old A</Logic>
            </Document>
            """;
        const string staleExport = """
            <Document>
              <Created>2026-10-05T02:54:23Z</Created>
              <Logic>old A</Logic>
            </Document>
            """;
        const string comparedExport = """
            <Document>
              <Created>2026-10-06T00:00:00Z</Created>
              <Logic>new A</Logic>
            </Document>
            """;
        File.WriteAllText(fixture.MasterSource("Blocks/A.xml"), baselineExport);
        File.WriteAllText(fixture.StagingSource("Blocks/A.xml"), staleExport);
        fixture.SetLiveDifferenceFingerprint("Blocks/A.xml", comparedExport);
        SeedSourceManifest();
        var engineering = ExportEngineering(comparedExport);
        var coordinator = fixture.CreateCoordinator(engineering);

        await coordinator.ApplyTiaSynchronizationAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            fixture.ComparisonId,
            [fixture.Path("Blocks/A.xml")],
            "Accept the compared block",
            CancellationToken.None);

        Assert.Contains("export_source_object", engineering.Calls);
        Assert.Equal(comparedExport, File.ReadAllText(fixture.MasterSource("Blocks/A.xml")));
    }

    [Fact]
    public async Task ApplyRefusesWhenTiaNoLongerMatchesTheComparison()
    {
        // A re-export that still does not match the compared content means TIA moved on (or the
        // comparison carries no live evidence for this object). Fail closed: never commit content
        // the user did not see as accepted.
        File.WriteAllText(fixture.MasterSource("Blocks/A.xml"), "old A");
        File.WriteAllText(fixture.StagingSource("Blocks/A.xml"), "even older A");
        fixture.SetLiveDifferenceFingerprint("Blocks/A.xml", "compared A");
        SeedSourceManifest();
        var engineering = ExportEngineering("changed again A");
        var coordinator = fixture.CreateCoordinator(engineering);

        var exception = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() => coordinator.ApplyTiaSynchronizationAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            fixture.ComparisonId,
            [fixture.Path("Blocks/A.xml")],
            "Accept a stale comparison",
            CancellationToken.None));

        Assert.Equal("TIA_SOURCE_CHANGED_AFTER_COMPARISON", exception.Code);
        Assert.Equal("old A", File.ReadAllText(fixture.MasterSource("Blocks/A.xml")));
        Assert.DoesNotContain("vc_commit_selected", fixture.VersionControl.Calls);
    }

    [Fact]
    public async Task ApplyReusesACanonicalStagingCopyThatAlreadyIsTheComparedContent()
    {
        var engineering = ExportEngineering("should not be exported");
        var coordinator = fixture.CreateCoordinator(engineering);

        var result = await coordinator.ApplyTiaSynchronizationAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            fixture.ComparisonId,
            [fixture.Path("Blocks/A.xml")],
            "Accept the staged block",
            CancellationToken.None);

        Assert.Equal("head-2", result.CommitSha);
        Assert.DoesNotContain("export_source_object", engineering.Calls);
        Assert.Equal("new A", File.ReadAllText(fixture.MasterSource("Blocks/A.xml")));
    }

    [Fact]
    public async Task ApplyOfEverySourceDifferenceCreatesAConsistentFingerprintBaseline()
    {
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new Contracts.Engineering.ProjectInfo
            {
                Name = "Line",
                Path = SyncFixture.ProjectPath,
                PlcDevices = ["PLC_1"],
            })
            .Respond("export_source_object", new Contracts.Engineering.ExportResult { Success = true })
            .Respond("capture_source_evidence", new Contracts.Engineering.SourceEvidenceCaptureResult
            {
                Snapshot = new Contracts.Engineering.SourceEvidenceSnapshot
                {
                    PlcName = "PLC_1",
                    Checksum = new Contracts.Engineering.PlcChecksumInfo
                    {
                        PlcName = "PLC_1",
                        ProjectIdentity = "project-1",
                        SoftwareChecksum = "checksum-1",
                    },
                    Objects = new[]
                    {
                        new Contracts.Engineering.ManagedSourceEvidenceObject
                        {
                            Id = "main",
                            Name = "Main",
                            SourcePath = "Program blocks/Main",
                            Category = "OB",
                            Kind = Contracts.Engineering.ManagedSourceEvidenceKind.StandardBlock,
                            ReadState = Contracts.Engineering.ManagedSourceEvidenceReadState.Readable,
                            Fingerprints = new Contracts.Engineering.FingerprintSet { ["code"] = "new" },
                        },
                    },
                },
            });
        var coordinator = fixture.CreateCoordinator(engineering);

        await coordinator.ApplyTiaSynchronizationAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            fixture.ComparisonId,
            [fixture.Path("Blocks/A.xml"), fixture.Path("Blocks/B.xml")],
            "Accept every TIA source change",
            CancellationToken.None);

        var evidence = fixture.VersionControl.ValidationEvidence;
        Assert.NotNull(evidence);
        Assert.Equal("2.0", evidence!.SchemaVersion);
        Assert.Equal("tia-managed-source", evidence.EvidenceKind);
        Assert.True(evidence.ManagedSourceConsistent);
    }

    [Fact]
    public async Task MasterCommitAllowsADirectLocalEdit()
    {
        var coordinator = fixture.CreateCoordinator();
        await coordinator.ApplyTiaSynchronizationAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            fixture.ComparisonId,
            [fixture.Path("Blocks/A.xml")],
            "Accept A",
            CancellationToken.None);
        File.WriteAllText(fixture.MasterSource("Blocks/A.xml"), "local edit");

        // Direct local edits on master are allowed (MASTER_EDIT_NOT_ALLOWED and the
        // TIA-authorization requirement are disabled); they commit as unlabeled savepoints.
        var result = await coordinator.CommitSourceAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            [fixture.Path("Blocks/A.xml")],
            "direct edit",
            CancellationToken.None);

        Assert.Equal("head-2", result.Sha);
        Assert.Equal("direct edit", fixture.VersionControl.CommitMessage);
    }

    [Fact]
    public async Task MasterCommitWithUntrackableChangePermitsEmptyPathsAndForwardsFlags()
    {
        var coordinator = fixture.CreateCoordinator();

        var result = await coordinator.CommitSourceAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            Array.Empty<string>(),
            "TIA change git cannot track",
            CancellationToken.None,
            untrackableChange: true);

        Assert.Equal("head-2", result.Sha);
        Assert.Equal("TIA change git cannot track", fixture.VersionControl.CommitMessage);
        var commitArgs = Assert.IsAssignableFrom<object>(fixture.VersionControl.CommitArgs);
        Assert.Empty(Property<IReadOnlyList<string>>(commitArgs, "paths"));
        Assert.True(Property<bool>(commitArgs, "allowEmpty"));
        Assert.True(Property<bool>(commitArgs, "untrackableChange"));
    }

    [Fact]
    public async Task MasterCommitWithoutUntrackableChangeStillRequiresPaths()
    {
        var coordinator = fixture.CreateCoordinator();

        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.CommitSourceAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            Array.Empty<string>(),
            "message-only without the flag",
            CancellationToken.None));

        Assert.DoesNotContain("vc_commit_selected", fixture.VersionControl.Calls);
    }

    [Fact]
    public async Task MasterCommitRecordsTheLiveTiaChecksumStateOnTheCommit()
    {
        var coordinator = fixture.CreateCoordinator(TiaEngineering());

        var result = await coordinator.CommitSourceAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            [fixture.Path("Blocks/A.xml")],
            "direct edit",
            CancellationToken.None);

        Assert.Equal("head-2", result.Sha);
        Assert.Contains("vc_commit_state_create", fixture.VersionControl.Calls);
        var devices = Assert.IsAssignableFrom<System.Collections.IEnumerable>(
            Property<object>(fixture.VersionControl.StateCreateArgs!, "devices"));
        var device = Assert.Single(devices.Cast<object>());
        Assert.Equal("device-1", Property<string>(device, "deviceId"));
        Assert.Equal("PLC_1", Property<string>(device, "plcName"));
        Assert.Equal("abc123", Property<string>(device, "projectChecksum"));
    }

    [Fact]
    public async Task MasterCommitWithUntrackableChangeRecordsTheLiveTiaChecksumState()
    {
        var coordinator = fixture.CreateCoordinator(TiaEngineering());

        var result = await coordinator.CommitSourceAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            Array.Empty<string>(),
            "TIA change git cannot track",
            CancellationToken.None,
            untrackableChange: true);

        Assert.Equal("head-2", result.Sha);
        Assert.Contains("vc_commit_state_create", fixture.VersionControl.Calls);
    }

    [Fact]
    public async Task UntrackableCommitCarriesForwardAnAlreadyProvenManagedSourceBaseline()
    {
        fixture.VersionControl.SeedValidation(new TiaSyncEvidence
        {
            SchemaVersion = "2.0",
            EvidenceKind = "tia-managed-source",
            CommitSha = "head-1",
            ManagedSourceConsistent = true,
            Devices = new[] { new TiaSyncEvidenceDevice { DeviceId = "device-1" } },
        });
        var coordinator = fixture.CreateCoordinator(TiaEngineering());

        await coordinator.CommitSourceAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            Array.Empty<string>(),
            "native-only TIA change",
            CancellationToken.None,
            untrackableChange: true);

        Assert.NotNull(fixture.VersionControl.ValidationEvidence);
        Assert.True(fixture.VersionControl.ValidationEvidence!.ManagedSourceConsistent);
    }

    [Fact]
    public async Task MasterCommitSucceedsWithoutChecksumStateWhenTiaIsUnavailable()
    {
        var coordinator = fixture.CreateCoordinator();

        var result = await coordinator.CommitSourceAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            [fixture.Path("Blocks/A.xml")],
            "direct edit",
            CancellationToken.None);

        Assert.Equal("head-2", result.Sha);
        Assert.DoesNotContain("vc_commit_state_create", fixture.VersionControl.Calls);
    }

    [Fact]
    public async Task MasterCommitWithSafetyChangeAdvancesTheSourceManifestSafetyBaseline()
    {
        var masterRoot = System.IO.Path.Combine(fixture.Root, "worktrees", "master");
        // Seed a tracked source manifest with the old safety baseline plus a marker component
        // that must survive the baseline advance.
        File.WriteAllText(fixture.MasterSource("metadata.json"), """
            {
              "schemaVersion": "1.0",
              "device": {
                "plcName": "PLC_1",
                "isSafetyDevice": true,
                "fSignatureReadState": "ok",
                "fSignature": "OLDSIG",
                "fBlockSignatures": [
                  { "path": "Program blocks/Safety/FbA", "signature": "AAAAAAAA" }
                ]
              },
              "components": [ { "id": "keep-me" } ]
            }
            """);
        var engineering = SafetyEngineering();
        var coordinator = fixture.CreateCoordinator(engineering);

        var result = await coordinator.CommitSourceAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            Array.Empty<string>(),
            "Record safety change",
            CancellationToken.None,
            safetyChange: true);

        Assert.Equal("head-2", result.Sha);
        var baseline = DeviceManifestSafety.TryReadBaseline(
            System.IO.Path.GetDirectoryName(fixture.MasterSource("metadata.json"))!);
        Assert.NotNull(baseline);
        Assert.Equal("NEWSIG", baseline!.FSignature);
        Assert.Equal(2, baseline.BlockSignatures!.Count);
        Assert.Contains(baseline.BlockSignatures, block =>
            block.Path == "Program blocks/Safety/FbB" && block.Signature == "CCCCCCCC");
        // The rest of the manifest is preserved; a safety commit never touches revision.json.
        Assert.Contains("keep-me", File.ReadAllText(fixture.MasterSource("metadata.json")));
        Assert.False(File.Exists(WorkbenchPaths.ResolveRevisionState(masterRoot)));
        var commitArgs = Assert.IsAssignableFrom<object>(fixture.VersionControl.CommitArgs);
        Assert.Contains(
            "devices/PLC_1/source/metadata.json",
            Property<IReadOnlyList<string>>(commitArgs, "paths"));
        Assert.True(Property<bool>(commitArgs, "safetyChange"));
        // The live checksums read for the baseline are reused for the commit-state tag.
        Assert.Single(engineering.CallArgs["get_plc_checksums"]);
        Assert.Contains("vc_commit_state_create", fixture.VersionControl.Calls);
    }

    [Fact]
    public async Task MasterCommitWithSafetyChangeAbortsWhenLiveEvidenceIsUnreadable()
    {
        var masterRoot = System.IO.Path.Combine(fixture.Root, "worktrees", "master");
        var coordinator = fixture.CreateCoordinator();

        var exception = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() => coordinator.CommitSourceAsync(
            fixture.Workbench.WorkbenchId,
            fixture.Master.WorktreeId,
            Array.Empty<string>(),
            "Record safety change",
            CancellationToken.None,
            safetyChange: true));

        Assert.Equal("SAFETY_EVIDENCE_UNAVAILABLE", exception.Code);
        Assert.DoesNotContain("vc_commit_selected", fixture.VersionControl.Calls);
        Assert.False(File.Exists(fixture.MasterSource("metadata.json")));
        Assert.False(File.Exists(WorkbenchPaths.ResolveRevisionState(masterRoot)));
    }

    /// <summary>
    /// The tracked export manifest that lets the coordinator resolve one object's TIA identity for
    /// the re-export (a plain "Blocks/A.xml" path carries no category on its own).
    /// </summary>
    private void SeedSourceManifest() =>
        File.WriteAllText(fixture.MasterSource("metadata.json"), """
            { "components": [
                { "id": "a", "name": "A", "category": "FC", "exportedFile": "Blocks/A.xml" },
                { "id": "b", "name": "B", "category": "FC", "exportedFile": "Blocks/B.xml" }
            ] }
            """);

    /// <summary>
    /// A TIA session whose <c>export_source_object</c> writes <paramref name="content"/> into the
    /// device staging root — the way the real exporter materializes one selected object.
    /// </summary>
    private static FakeToolCaller ExportEngineering(string content) =>
        new FakeToolCaller()
            .Respond("get_project_info", new Contracts.Engineering.ProjectInfo
            {
                Name = "Line",
                Path = SyncFixture.ProjectPath,
                PlcDevices = ["PLC_1"],
            })
            .Respond("export_source_object", args =>
            {
                var outputDir = (string)args.GetType().GetProperty("outputDir")!.GetValue(args)!;
                var exported = System.IO.Path.Combine(outputDir, "Blocks", "A.xml");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(exported)!);
                File.WriteAllText(exported, content);
                return new Contracts.Engineering.ExportResult { Success = true, Path = exported };
            });

    private static FakeToolCaller SafetyEngineering() =>
        new FakeToolCaller()
            .Respond("get_project_info", new Contracts.Engineering.ProjectInfo
            {
                Name = "Line",
                Path = SyncFixture.ProjectPath,
                PlcDevices = ["PLC_1"],
            })
            .Respond("get_plc_checksums", new[]
            {
                new Contracts.Engineering.PlcChecksumInfo
                {
                    PlcName = "PLC_1",
                    ProjectIdentity = "project-1",
                    SoftwareChecksum = "abc123",
                    IsSafetyDevice = true,
                    FSignatureReadState = Contracts.Engineering.FSignatureReadState.Ok,
                    FSignature = "NEWSIG",
                    FBlockSignatures = new[]
                    {
                        new Contracts.Engineering.FBlockSignatureInfo { Path = "Program blocks/Safety/FbA", Signature = "BBBBBBBB" },
                        new Contracts.Engineering.FBlockSignatureInfo { Path = "Program blocks/Safety/FbB", Signature = "CCCCCCCC" },
                    },
                },
            })
            .Respond("get_project_info", new Contracts.Engineering.ProjectInfo
            {
                Name = "Line",
                Path = SyncFixture.ProjectPath,
                PlcDevices = ["PLC_1"],
            })
            .Respond("capture_source_evidence", new Contracts.Engineering.SourceEvidenceCaptureResult
            {
                Snapshot = new Contracts.Engineering.SourceEvidenceSnapshot
                {
                    PlcName = "PLC_1",
                    Checksum = new Contracts.Engineering.PlcChecksumInfo
                    {
                        PlcName = "PLC_1",
                        ProjectIdentity = "project-1",
                        SoftwareChecksum = "abc123",
                    },
                    Objects = Array.Empty<Contracts.Engineering.ManagedSourceEvidenceObject>(),
                },
            });

    private static FakeToolCaller TiaEngineering() =>
        new FakeToolCaller()
            .Respond("get_project_info", new Contracts.Engineering.ProjectInfo
            {
                Name = "Line",
                Path = SyncFixture.ProjectPath,
                PlcDevices = ["PLC_1"],
            })
            .Respond("get_plc_checksums", new[]
            {
                new Contracts.Engineering.PlcChecksumInfo { PlcName = "PLC_1", SoftwareChecksum = "abc123" },
            })
            .Respond("get_project_info", new Contracts.Engineering.ProjectInfo
            {
                Name = "Line",
                Path = SyncFixture.ProjectPath,
                PlcDevices = ["PLC_1"],
            })
            .Respond("capture_source_evidence", new Contracts.Engineering.SourceEvidenceCaptureResult
            {
                Snapshot = new Contracts.Engineering.SourceEvidenceSnapshot
                {
                    PlcName = "PLC_1",
                    Checksum = new Contracts.Engineering.PlcChecksumInfo
                    {
                        PlcName = "PLC_1",
                        ProjectIdentity = "project-1",
                        SoftwareChecksum = "abc123",
                    },
                    Objects = Array.Empty<Contracts.Engineering.ManagedSourceEvidenceObject>(),
                },
            });

    public void Dispose() => fixture.Dispose();

    [Fact]
    public async Task PushSourcesToTiaImportsSelectedLocalObjectsIntoTia()
    {
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new Contracts.Engineering.ProjectInfo
            {
                Name = "Line",
                Path = SyncFixture.ProjectPath,
                PlcDevices = ["PLC_1"],
            })
            .Respond("import_source_object", new { success = true })
            .Respond("import_source_object", new { success = true });
        var coordinator = fixture.CreateCoordinator(engineering);

        var result = await coordinator.PushSourcesToTiaAsync(
            fixture.Workbench.WorkbenchId,
            fixture.ComparisonId,
            [fixture.Path("Blocks/A.xml"), fixture.Path("Blocks/B.xml")],
            CancellationToken.None);

        Assert.Equal(2, result.Outcomes.Count);
        Assert.All(result.Outcomes, outcome => Assert.True(outcome.Success));
        var imports = engineering.CallArgs["import_source_object"];
        Assert.Equal(2, imports.Count);
        Assert.Equal("Blocks/A.xml", Property<string>(imports[0], "relativePath"));
        Assert.Equal("Blocks/B.xml", Property<string>(imports[1], "relativePath"));
        Assert.Equal("PLC_1", Property<string>(imports[0], "plcName"));
    }

    private static T Property<T>(object value, string name) =>
        (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    private sealed class SyncFixture : IDisposable
    {
        private SyncFixture(
            string root,
            WorkbenchMetadata workbench,
            WorktreeMetadata master,
            AtomicJsonStore store,
            SyncVersionControlCaller versionControl)
        {
            Root = root;
            Workbench = workbench;
            Master = master;
            Store = store;
            VersionControl = versionControl;
        }

        public string Root { get; }
        public WorkbenchMetadata Workbench { get; }
        public WorktreeMetadata Master { get; }
        public AtomicJsonStore Store { get; }
        public SyncVersionControlCaller VersionControl { get; }
        public string ComparisonId => "comparison-1";
        public const string ProjectPath = @"C:\Projects\Line.ap17";

        public static SyncFixture Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "master-sync-tests", Guid.NewGuid().ToString("N"));
            var store = new AtomicJsonStore();
            var workbench = new WorkbenchMetadata(
                "1.0", "wb-1", "wb", "now", root, System.IO.Path.Combine(root, "repository.git"), "project-1", null,
                new[] { new WorkbenchWorktreeRegistration("master-1", "master", "master", "master") });
            var master = new WorktreeMetadata(
                "1.0", "master-1", "wb-1", "master", "master", "now", "head-1", "project-1", ProjectPath,
                new[] { "device-1" }, null);
            var masterRoot = System.IO.Path.Combine(root, "worktrees", "master");
            Directory.CreateDirectory(masterRoot);
            store.Write(System.IO.Path.Combine(root, "workbench.json"), workbench);
            store.Write(System.IO.Path.Combine(masterRoot, "worktree.json"), master);
            var context = WorkbenchPaths.ResolveDevice("wb-1", root, "master-1", "master", "device-1", "PLC_1");
            Directory.CreateDirectory(System.IO.Path.Combine(context.SourceRoot, "Blocks"));
            Directory.CreateDirectory(System.IO.Path.Combine(context.StagingRoot, "Blocks"));
            File.WriteAllText(System.IO.Path.Combine(context.SourceRoot, "Blocks", "A.xml"), "old A");
            File.WriteAllText(System.IO.Path.Combine(context.SourceRoot, "Blocks", "B.xml"), "old B");
            File.WriteAllText(System.IO.Path.Combine(context.StagingRoot, "Blocks", "A.xml"), "new A");
            File.WriteAllText(System.IO.Path.Combine(context.StagingRoot, "Blocks", "B.xml"), "new B");
            store.Write(System.IO.Path.Combine(context.DeviceRoot, "device.json"), new DeviceMetadata(
                "1.0", "device-1", "master-1", "PLC_1", "project-1", null, null, null,
                new KnowledgeState(false, new Dictionary<string, string>(), null), Array.Empty<DeviceImportRecord>()));

            var comparison = new WorkbenchConsistencyResult(
                "comparison-1",
                "head-1",
                false,
                ConsistencyState.Different,
                new Dictionary<string, string?> { ["device-1"] = "checksum-1" },
                new[]
                {
                    // Real managed-source identities, not placeholders: the accept path verifies
                    // that the staging copy is the content the comparison observed, so a fixture
                    // whose fingerprints do not describe the staged bytes would prove nothing
                    // (issue #111).
                    new SourceDifference("device-1", "PLC_1", "devices/PLC_1/source/Blocks/A.xml", "Blocks:A", SourceDifferenceKind.Changed, FingerprintOf("old A"), FingerprintOf("new A"), true),
                    new SourceDifference("device-1", "PLC_1", "devices/PLC_1/source/Blocks/B.xml", "Blocks:B", SourceDifferenceKind.Changed, FingerprintOf("old B"), FingerprintOf("new B"), true),
                },
                ComparedWorktreeId: "master-1");
            store.Write(ComparisonPathFor(root), comparison);
            return new SyncFixture(root, workbench, master, store, new SyncVersionControlCaller());
        }

        public static string ComparisonPathFor(string root) =>
            System.IO.Path.Combine(root, ".automation", "comparisons", "comparison-1.json");

        /// <summary>
        /// The managed-source identity of arbitrary XML content, resolved through the same reader
        /// the comparison uses (normalized XML: export timestamps are stripped). Deriving it by
        /// hand would let a stale export pass for the compared candidate.
        /// </summary>
        public static string FingerprintOf(string content)
        {
            var probeRoot = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "master-sync-fingerprint", Guid.NewGuid().ToString("N"));
            var probeFile = System.IO.Path.Combine(probeRoot, "Blocks", "probe.xml");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(probeFile)!);
            File.WriteAllText(probeFile, content);
            try
            {
                return new SourceTreeReader().TryReadRelative(probeRoot, "Blocks/probe.xml")!.Sha256;
            }
            finally
            {
                Directory.Delete(probeRoot, recursive: true);
            }
        }

        public string Path(string relative) => $"devices/PLC_1/source/{relative}";

        public string ComparisonPath => ComparisonPathFor(Root);

        /// <summary>
        /// Restates what TIA holds for one compared object, the way a later export would: the
        /// comparison's live fingerprint must always describe the content TIA hands back.
        /// </summary>
        public void SetLiveDifferenceFingerprint(string relativePath, string liveContent)
        {
            var comparison = Store.Read<WorkbenchConsistencyResult>(ComparisonPath);
            Store.Write(ComparisonPath, comparison with
            {
                Differences = comparison.Differences
                    .Select(difference => difference.RelativePath.EndsWith(relativePath, StringComparison.Ordinal)
                        ? difference with { TiaFingerprint = FingerprintOf(liveContent) }
                        : difference)
                    .ToArray(),
            });
        }

        public string StagingSource(string relative) =>
            System.IO.Path.Combine(Root, "worktrees", "master", "devices", "PLC_1", "staging", relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public string MasterSource(string relative) =>
            System.IO.Path.Combine(Root, "worktrees", "master", "devices", "PLC_1", "source", relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public WorkbenchCoordinator CreateCoordinator(Agent.Mcp.IMcpToolCaller? engineering = null)
        {
            var catalog = new WorkbenchCatalog(Store, System.IO.Path.Combine(Root, "catalog"));
            var coordinator = new WorkbenchCoordinator(
                engineering ?? new NoOpCaller(),
                new NoOpCaller(),
                VersionControl,
                catalog,
                Store,
                new DeviceReconciler(),
                new DeviceSourceResolver(_ => { }));
            coordinator.RegisterWorkbench(Workbench);
            return coordinator;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class NoOpCaller : IMcpToolCaller
    {
        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default) =>
            tool == "export_source_object"
                ? Task.FromResult((T)(object)new Contracts.Engineering.ExportResult { Success = true })
                : throw new InvalidOperationException(tool);
    }

    private sealed class SyncVersionControlCaller : IMcpToolCaller
    {
        public List<string> Calls { get; } = new();
        public string Head { get; private set; } = "head-1";
        public string? CommitMessage { get; private set; }
        public object? CommitArgs { get; private set; }
        public object? StateCreateArgs { get; private set; }
        public TiaSyncEvidence? ValidationEvidence { get; private set; }

        public void SeedValidation(TiaSyncEvidence evidence) => ValidationEvidence = evidence;

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (tool == "vc_log")
            {
                return Task.FromResult((T)(object)new ConsistencyLogResult
                {
                    Commits = new[] { new ConsistencyCommit { Sha = Head } },
                });
            }
            if (tool == "vc_commit_selected")
            {
                CommitArgs = args;
                CommitMessage = args.GetType().GetProperty("message")?.GetValue(args) as string;
                Head = "head-2";
                return Task.FromResult((T)(object)new WorkbenchCommitResult(
                    Head,
                    CommitMessage ?? string.Empty,
                    new[] { "devices/PLC_1/source/Blocks/A.xml" }));
            }
            if (tool == "vc_commit_state_get")
            {
                return Task.FromResult((T)(object)null!);
            }
            if (tool == "vc_validation_get")
            {
                if (typeof(T) == typeof(ConsistencyValidationEvidence))
                {
                    return Task.FromResult((T)(object)new ConsistencyValidationEvidence
                    {
                        SchemaVersion = ValidationEvidence?.SchemaVersion ?? string.Empty,
                        EvidenceKind = ValidationEvidence?.EvidenceKind ?? string.Empty,
                        CommitSha = ValidationEvidence?.CommitSha ?? string.Empty,
                        ManagedSourceConsistent = ValidationEvidence?.ManagedSourceConsistent,
                        Devices = (ValidationEvidence?.Devices ?? Array.Empty<TiaSyncEvidenceDevice>())
                            .Select(device => new ConsistencyValidationDevice { DeviceId = device.DeviceId })
                            .ToArray(),
                    });
                }

                return Task.FromResult((T)(object?)ValidationEvidence!);
            }
            if (tool == "vc_commit_state_create")
            {
                StateCreateArgs = args;
                return Task.FromResult((T)(object)new object());
            }
            if (tool == "vc_validation_create")
            {
                ValidationEvidence = (TiaSyncEvidence)args.GetType().GetProperty("evidence")!.GetValue(args)!;
                return Task.FromResult((T)(object)ValidationEvidence);
            }
            throw new InvalidOperationException(tool);
        }
    }
}
