using Agent.Mcp;
using Agent.Workbench;
using Contracts.Engineering;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Agent.Tests;

public sealed class WorkbenchConsistencyServiceTests : IDisposable
{
    private readonly ConsistencyFixture fixture = ConsistencyFixture.Create();

    [Fact]
    public async Task MatchingValidationChecksumsSkipEveryExport()
    {
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.Evidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.Equal(ConsistencyState.Consistent, result.State);
        Assert.True(result.FastGatePassed);
        Assert.DoesNotContain("sync_export", engineering.Calls);
        Assert.DoesNotContain("rebuild_export", engineering.Calls);
    }

    [Fact]
    public async Task MatchingFingerprintEvidenceChecksumsSkipTheSourceEvidenceScan()
    {
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.FingerprintEvidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.Equal(ConsistencyState.Consistent, result.State);
        Assert.True(result.FastGatePassed);
        Assert.DoesNotContain("compare_source_evidence", engineering.Calls);
    }

    [Fact]
    public async Task V2EvidenceReadsAllLightweightEvidenceAndExportsOnlyTheChangedCandidate()
    {
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.FingerprintEvidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "changed"), ("PLC_2", "two"))
        {
            SourceXml = "<Document><SW.Blocks.OB ID=\"1\" Comment=\"changed\" /></Document>",
        };
        engineering.ChangedEvidencePlcs.Add("PLC_1");
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.Equal(ConsistencyState.Different, result.State);
        var difference = Assert.Single(result.Differences, difference => difference.PlcName == "PLC_1" && difference.Kind == SourceDifferenceKind.Changed);
        Assert.NotNull(difference.FingerprintComponents);
        Assert.False(difference.FingerprintComponents!["Code"].Matches);
        Assert.True(difference.FingerprintComponents["Comments"].Matches);
        Assert.Equal(2, engineering.Calls.Count(call => call == "compare_source_evidence"));
        Assert.DoesNotContain("sync_export", engineering.Calls);
        Assert.DoesNotContain("rebuild_export", engineering.Calls);
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, ".fingerprint-candidates-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FullXmlCompareUsesManifestFingerprintComponents()
    {
        fixture.WriteSourceManifest("PLC_1", """
            {
              "components": [
                {
                  "name": "Main",
                  "category": "OB",
                  "exportedFile": "Blocks/Main.xml",
                  "fingerprints": { "Code": "baseline-code", "Comments": "same-comments" }
                }
              ]
            }
            """);
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"))
        {
            SourceXmlByPlc = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PLC_1"] = "<Document><SW.Blocks.OB ID=\"1\" Comment=\"changed\" /></Document>",
                ["PLC_2"] = "<Document><SW.Blocks.OB ID=\"1\" /></Document>",
            },
            ExportMetadataJson = """
                {
                  "components": [
                    {
                      "name": "Main",
                      "category": "OB",
                      "exportedFile": "Blocks/Main.xml",
                      "fingerprints": { "Code": "live-code", "Comments": "same-comments" }
                    }
                  ]
                }
                """
        };
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        var difference = Assert.Single(result.Differences, item => item.PlcName == "PLC_1");
        Assert.NotNull(difference.FingerprintComponents);
        Assert.False(difference.FingerprintComponents!["Code"].Matches);
        Assert.True(difference.FingerprintComponents["Comments"].Matches);
    }

    [Fact]
    public async Task V2EvidenceMarksChangedTagTablesForContentHashDisplay()
    {
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.FingerprintEvidence(ManagedSourceEvidenceKind.TagTable));
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "changed"), ("PLC_2", "two"))
        {
            SourceXml = "<Document><SW.Tags.PlcTagTable ID=\"1\" Name=\"Plant\" /></Document>",
        };
        engineering.ChangedEvidencePlcs.Add("PLC_1");
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        var difference = Assert.Single(result.Differences, difference => difference.PlcName == "PLC_1");
        Assert.Equal(ManagedSourceEvidenceKind.TagTable, difference.EvidenceKind);
        Assert.Null(difference.FingerprintComponents);
        Assert.Equal(
            XmlContentHash.Compute("<Document><SW.Blocks.OB ID=\"1\" /></Document>"),
            difference.MasterFingerprint);
        Assert.Equal(
            XmlContentHash.Compute(engineering.SourceXml),
            difference.TiaFingerprint);
    }

    [Fact]
    public async Task CompareReturnsPhaseTimingsForAProfilingReport()
    {
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.Evidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        var timings = Assert.IsAssignableFrom<IReadOnlyList<ComparisonTiming>>(result.Timings);
        Assert.NotEmpty(timings);
        Assert.Contains(timings, timing => timing.Phase == "hardware-export");
        Assert.Contains(timings, timing =>
            timing.Phase == "hardware-aml-export"
            && timing.ElapsedMilliseconds == 1234
            && timing.Outcome == "Project CAx export completed.");
        Assert.Contains(timings, timing =>
            timing.Phase == "hardware-network-fingerprint"
            && timing.ElapsedMilliseconds == 567);
        Assert.Contains(timings, timing =>
            timing.Phase == "hardware-local-compare"
            && timing.Outcome == "Compared 1 hardware artifact(s).");
        Assert.Contains(timings, timing => timing.Phase == "checksum-read");
        Assert.All(timings, timing => Assert.True(timing.ElapsedMilliseconds >= 0));
    }

    [Fact]
    public async Task SkippingHardwareVerificationKeepsSourceAndSafetyCoverageWithoutExportingAml()
    {
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.FingerprintEvidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(
            fixture.Workbench,
            fixture.Master,
            CancellationToken.None,
            includeHardware: false);

        Assert.Equal(ConsistencyState.Consistent, result.State);
        Assert.False(result.HardwareChecked);
        Assert.Null(result.Hardware);
        Assert.DoesNotContain("export_hardware_configuration", engineering.Calls);
        Assert.Contains(result.Timings!, timing =>
            timing.Phase == "hardware-export"
            && timing.Outcome == "Hardware verification was not checked.");
        Assert.True(result.FastGatePassed);
        Assert.DoesNotContain("compare_source_evidence", engineering.Calls);
    }

    [Fact]
    public async Task UntrackableCommitDoesNotMaskPendingTrackableDiff()
    {
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.Evidence())
        {
            UntrackableChange = true,
        };
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"))
        {
            SourceXml = "<Document><SW.Blocks.OB ID=\"1\" Comment=\"changed\" /></Document>",
        };
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.False(result.FastGatePassed);
        Assert.Equal(ConsistencyState.Different, result.State);
        Assert.Contains(result.Differences, difference => difference.Kind == SourceDifferenceKind.Changed);
        Assert.Equal(2, engineering.Calls.Count(call => call == "sync_export"));
    }

    [Fact]
    public async Task ExplicitlyUntrustedEvidenceCannotPassChecksumFastGate()
    {
        var evidence = fixture.Evidence();
        evidence.ManagedSourceConsistent = false;
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, evidence);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.False(result.FastGatePassed);
        Assert.Equal(2, engineering.Calls.Count(call => call == "sync_export"));
    }

    [Fact]
    public async Task UnlabeledMasterScansEveryDeviceWithSyncExport()
    {
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.False(result.FastGatePassed);
        Assert.Equal(2, engineering.Calls.Count(call => call == "sync_export"));
    }

    [Fact]
    public async Task UnlabeledMasterCanForceFullRebuildExport()
    {
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None, forceFullExport: true);

        Assert.False(result.FastGatePassed);
        Assert.Equal(2, engineering.Calls.Count(call => call == "rebuild_export"));
    }

    [Fact]
    public async Task DirtyMasterSourceCannotPassFastGate()
    {
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.Evidence())
        {
            DirtySource = true,
        };
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.False(result.FastGatePassed);
        Assert.Equal(2, engineering.Calls.Count(call => call == "sync_export"));
    }

    [Fact]
    public async Task ProjectHardwareDifferenceIsReportedByFullComparison()
    {
        var hardwareRoot = WorkbenchPaths.ResolveHardwareRoot(fixture.Root + "\\worktrees\\master");
        Directory.CreateDirectory(hardwareRoot);
        var savedXml = "<CAEXFile><Device Name=\"PLC_1\" /></CAEXFile>";
        File.WriteAllText(Path.Combine(hardwareRoot, "project.aml"), savedXml);
        File.WriteAllText(
            Path.Combine(hardwareRoot, "manifest.json"),
            JsonSerializer.Serialize(new { projectContentHash = XmlContentHash.Compute(savedXml) }));

        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"))
        {
            ProjectXml = "<CAEXFile><Device Name=\"PLC_2\" /></CAEXFile>",
        };
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.NotNull(result.Hardware);
        Assert.Equal("changed", result.Hardware!.State);
        Assert.Equal(ConsistencyState.Different, result.State);
        Assert.Contains("export_hardware_configuration", engineering.Calls);
    }

    [Fact]
    public async Task ProjectHardwareComparisonRehashesSavedAmlWithCurrentNormalization()
    {
        var hardwareRoot = WorkbenchPaths.ResolveHardwareRoot(fixture.Root + "\\worktrees\\master");
        Directory.CreateDirectory(hardwareRoot);
        var savedXml = "<CAEXFile>\n  <LastWritingDateTime>2026-08-17T06:43:36Z</LastWritingDateTime>\n  <Device Ip=\"192.168.0.5\" />\n</CAEXFile>";
        var liveXml = "<CAEXFile>\n  <LastWritingDateTime>2026-08-17T08:44:33Z</LastWritingDateTime>\n  <Device Ip=\"192.168.0.5\" />\n</CAEXFile>";
        File.WriteAllText(Path.Combine(hardwareRoot, "project.aml"), savedXml);
        File.WriteAllText(
            Path.Combine(hardwareRoot, "manifest.json"),
            JsonSerializer.Serialize(new { projectContentHash = LegacyHardwareHash(savedXml) }));

        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"))
        {
            ProjectXml = liveXml,
        };
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.NotNull(result.Hardware);
        Assert.Equal("in-sync", result.Hardware!.State);
    }

    [Fact]
    public async Task ProjectHardwareComparisonReportsIpDifferenceAfterTimestampNormalization()
    {
        var hardwareRoot = WorkbenchPaths.ResolveHardwareRoot(fixture.Root + "\\worktrees\\master");
        Directory.CreateDirectory(hardwareRoot);
        var savedXml = "<CAEXFile>\n  <LastWritingDateTime>2026-08-17T06:43:36Z</LastWritingDateTime>\n  <Device Ip=\"192.168.0.5\" />\n</CAEXFile>";
        var liveXml = "<CAEXFile>\n  <LastWritingDateTime>2026-08-17T08:44:33Z</LastWritingDateTime>\n  <Device Ip=\"192.168.0.15\" />\n</CAEXFile>";
        File.WriteAllText(Path.Combine(hardwareRoot, "project.aml"), savedXml);
        File.WriteAllText(
            Path.Combine(hardwareRoot, "manifest.json"),
            JsonSerializer.Serialize(new { projectContentHash = LegacyHardwareHash(savedXml) }));

        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"))
        {
            ProjectXml = liveXml,
        };
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.NotNull(result.Hardware);
        Assert.Equal("changed", result.Hardware!.State);
        Assert.Equal("changed", Assert.Single(result.Hardware.Artifacts).State);
    }

    [Fact]
    public async Task ValidateSyncCreatesPermanentTiaEvidenceOnlyAfterExactScan()
    {
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var evidence = await service.ValidateSynchronizedMasterAsync(
            fixture.Workbench,
            fixture.Master,
            "Test User <test@example.local>",
            CancellationToken.None);

        Assert.Equal("tia-managed-source", evidence.EvidenceKind);
        Assert.True(evidence.ManagedSourceConsistent);
        Assert.False(evidence.MachineValidated);
        Assert.Equal(2, evidence.Devices.Count);
        Assert.Contains("vc_validation_create", versionControl.Calls);
    }

    [Fact]
    public async Task SafetySignatureChangeMakesCompareDifferentEvenWhenEverythingElseMatches()
    {
        fixture.WriteBaselineFSignature("PLC_1:AAAA1111");
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.Evidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        engineering.Safety["PLC_1"] = (true, FSignatureReadState.Ok, "BBBB2222", null);
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.True(result.FastGatePassed);
        Assert.Equal(ConsistencyState.Different, result.State);
        Assert.True(result.SafetyChanged);
        var plc1 = Assert.Single(result.Safety!, item => item.PlcName == "PLC_1");
        Assert.True(plc1.IsSafetyDevice);
        Assert.Equal(FSignatureReadState.Ok, plc1.ReadState);
        Assert.Equal("BBBB2222", plc1.FSignature);
        Assert.Equal("AAAA1111", plc1.BaselineFSignature);
        Assert.True(plc1.Changed);
        Assert.DoesNotContain("sync_export", engineering.Calls);
    }

    [Fact]
    public async Task FailedSafetySignatureReadIsNeverReportedConsistent()
    {
        // Safety device whose required signature read failed; baseline recorded no signature.
        fixture.WriteBaselineFSignature(null);
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.Evidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        engineering.Safety["PLC_1"] = (true, FSignatureReadState.ReadFailed, null, null);
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.Equal(ConsistencyState.Unavailable, result.State);
        Assert.False(result.SafetyChanged);
        var plc1 = Assert.Single(result.Safety!, item => item.PlcName == "PLC_1");
        Assert.True(plc1.IsSafetyDevice);
        Assert.Equal(FSignatureReadState.ReadFailed, plc1.ReadState);
        Assert.Null(plc1.FSignature);
    }

    [Fact]
    public async Task MissingLiveSignatureAgainstBaselineIsUnavailableNotPhantomChange()
    {
        // Degraded-evidence scenario: baseline recorded a signature, the live device is still a
        // safety device, but no live signature was read (e.g. the safety program was never
        // compiled in this session). Degraded evidence must surface as Unavailable, never as
        // Different (phantom change) and never as Consistent.
        fixture.WriteBaselineFSignature("PLC_1:AAAA1111");
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.Evidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        engineering.Safety["PLC_1"] = (true, FSignatureReadState.NoSignature, null, null);
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.Equal(ConsistencyState.Unavailable, result.State);
        Assert.False(result.SafetyChanged);
        var plc1 = Assert.Single(result.Safety!, item => item.PlcName == "PLC_1");
        Assert.True(plc1.IsSafetyDevice);
        Assert.Equal(FSignatureReadState.NoSignature, plc1.ReadState);
        Assert.Null(plc1.FSignature);
        Assert.Equal("AAAA1111", plc1.BaselineFSignature);
        Assert.False(plc1.Changed);
    }

    [Fact]
    public async Task PerBlockSignaturesAttributeTheSafetyChangeToChangedBlocks()
    {
        fixture.WriteBaselineSafetyDevices(new EngineeringSafetyDevice(
            "PLC_1",
            FSignatureReadState.Ok,
            "fold-old",
            new[]
            {
                new FBlockSignatureInfo { Path = "Program blocks/F_Main", Signature = "AAAA1111" },
                new FBlockSignatureInfo { Path = "Program blocks/F_Output", Signature = "CCCC3333" },
            }));
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.Evidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        engineering.Safety["PLC_1"] = (true, FSignatureReadState.Ok, "fold-new", new[]
        {
            new FBlockSignatureInfo { Path = "Program blocks/F_Main", Signature = "BBBB2222" },
            new FBlockSignatureInfo { Path = "Program blocks/F_Output", Signature = "CCCC3333" },
        });
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.Equal(ConsistencyState.Different, result.State);
        Assert.True(result.SafetyChanged);
        var plc1 = Assert.Single(result.Safety!, item => item.PlcName == "PLC_1");
        Assert.True(plc1.Changed);
        Assert.Equal("fold-old", plc1.BaselineFSignature);
        var changedBlock = Assert.Single(plc1.ChangedBlocks!);
        Assert.Equal("Program blocks/F_Main", changedBlock);
        Assert.Equal(2, plc1.FBlockSignatures!.Count);
        Assert.DoesNotContain("sync_export", engineering.Calls);
    }

    [Fact]
    public async Task MatchingPerBlockSignaturesKeepCompareConsistent()
    {
        var blocks = new[]
        {
            new FBlockSignatureInfo { Path = "Program blocks/F_Main", Signature = "AAAA1111" },
        };
        fixture.WriteBaselineSafetyDevices(new EngineeringSafetyDevice(
            "PLC_1", FSignatureReadState.Ok, "fold-same", blocks));
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.Evidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        engineering.Safety["PLC_1"] = (true, FSignatureReadState.Ok, "fold-same", blocks);
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.Equal(ConsistencyState.Consistent, result.State);
        Assert.False(result.SafetyChanged);
        var plc1 = Assert.Single(result.Safety!, item => item.PlcName == "PLC_1");
        Assert.False(plc1.Changed);
        Assert.Empty(plc1.ChangedBlocks!);
    }

    [Fact]
    public async Task MatchingInvalidatedPerBlockSignaturesKeepCompareConsistent()
    {
        var blocks = new[]
        {
            new FBlockSignatureInfo { Path = "Program blocks/F_Main", Signature = "00000000" },
        };
        fixture.WriteBaselineSafetyDevices(new EngineeringSafetyDevice(
            "PLC_1", FSignatureReadState.Ok, "fold-same", blocks));
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.Evidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        engineering.Safety["PLC_1"] = (true, FSignatureReadState.Ok, "fold-same", blocks);
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.False(result.SafetyChanged);
        Assert.Empty(Assert.Single(result.Safety!, item => item.PlcName == "PLC_1").ChangedBlocks!);
    }

    [Fact]
    public async Task LostSafetyApplicabilityCountsAsSafetyChange()
    {
        // Baseline recorded an F-signature for PLC_1; the live read no longer reports a safety
        // surface at all (slow path: no validation evidence, so devices are scanned and match).
        fixture.WriteBaselineFSignature("PLC_1:AAAA1111");
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.False(result.FastGatePassed);
        Assert.Empty(result.Differences);
        Assert.Equal(ConsistencyState.Different, result.State);
        Assert.True(result.SafetyChanged);
        var plc1 = Assert.Single(result.Safety!, item => item.PlcName == "PLC_1");
        Assert.False(plc1.IsSafetyDevice);
        Assert.True(plc1.Changed);
    }

    [Fact]
    public async Task ManifestSafetyBaselineTakesPrecedenceOverRevisionState()
    {
        // The manifest baseline matches live; the stale revision.json baseline must be ignored.
        fixture.WriteBaselineFSignature("PLC_1:STALE1111");
        fixture.WriteSourceManifest("PLC_1", """
            { "device": { "plcName": "PLC_1", "isSafetyDevice": true, "fSignatureReadState": "ok", "fSignature": "BBBB2222" } }
            """);
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.Evidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        engineering.Safety["PLC_1"] = (true, FSignatureReadState.Ok, "BBBB2222", null);
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.Equal(ConsistencyState.Consistent, result.State);
        Assert.False(result.SafetyChanged);
        var plc1 = Assert.Single(result.Safety!, item => item.PlcName == "PLC_1");
        Assert.False(plc1.Changed);
        Assert.Equal("BBBB2222", plc1.BaselineFSignature);
    }

    [Fact]
    public async Task ManifestBaselineAttributesTheSafetyChangeToChangedBlocks()
    {
        fixture.WriteSourceManifest("PLC_1", """
            {
              "device": {
                "plcName": "PLC_1",
                "isSafetyDevice": true,
                "fSignatureReadState": "ok",
                "fSignature": "fold-old",
                "fBlockSignatures": [
                  { "path": "Program blocks/F_Main", "signature": "AAAA1111" },
                  { "path": "Program blocks/F_Output", "signature": "CCCC3333" }
                ]
              }
            }
            """);
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.Evidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        engineering.Safety["PLC_1"] = (true, FSignatureReadState.Ok, "fold-new", new[]
        {
            new FBlockSignatureInfo { Path = "Program blocks/F_Main", Signature = "BBBB2222" },
            new FBlockSignatureInfo { Path = "Program blocks/F_Output", Signature = "CCCC3333" },
        });
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.Equal(ConsistencyState.Different, result.State);
        Assert.True(result.SafetyChanged);
        var plc1 = Assert.Single(result.Safety!, item => item.PlcName == "PLC_1");
        Assert.True(plc1.Changed);
        Assert.Equal("fold-old", plc1.BaselineFSignature);
        Assert.Equal("Program blocks/F_Main", Assert.Single(plc1.ChangedBlocks!));
    }

    [Fact]
    public async Task ManifestWithoutSafetyIdentityFallsBackToTheRevisionStateBaseline()
    {
        // A manifest without safety identity (pre-safety export) is not a baseline: the legacy
        // revision.json baseline applies until a safety-accepting commit writes the manifest.
        fixture.WriteBaselineFSignature("PLC_1:AAAA1111");
        fixture.WriteSourceManifest("PLC_1", """{ "device": { "plcName": "PLC_1" } }""");
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, fixture.Evidence());
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        engineering.Safety["PLC_1"] = (true, FSignatureReadState.Ok, "BBBB2222", null);
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.Equal(ConsistencyState.Different, result.State);
        Assert.True(result.SafetyChanged);
        Assert.Equal("AAAA1111", Assert.Single(result.Safety!, item => item.PlcName == "PLC_1").BaselineFSignature);
    }

    public void Dispose() => fixture.Dispose();

    private static string LegacyHardwareHash(string xml)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(xml.Replace("\r", "")));
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private sealed class ConsistencyVersionControlCaller : IMcpToolCaller
    {
        private readonly string head;
        private readonly ConsistencyValidationEvidence? evidence;

        public ConsistencyVersionControlCaller(string head, ConsistencyValidationEvidence? evidence)
        {
            this.head = head;
            this.evidence = evidence;
        }

        public bool DirtySource { get; set; }
        public bool UntrackableChange { get; set; }
        public List<string> Calls { get; } = new();

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            object result = tool switch
            {
                "vc_log" => new ConsistencyLogResult { Commits = new[] { new ConsistencyCommit { Sha = head } } },
                "vc_validation_get" => evidence!,
                "vc_untrackable_change_get" => new TimelineUntrackableChangeResult
                {
                    UntrackableChange = UntrackableChange,
                },
                "vc_status" => new ConsistencyStatusResult
                {
                    Entries = DirtySource
                        ? new[] { new ConsistencyStatusEntry { FilePath = "devices/PLC_1/source/Blocks/Main.xml" } }
                        : Array.Empty<ConsistencyStatusEntry>(),
                },
                "vc_validation_create" => args.GetType().GetProperty("evidence")!.GetValue(args)!,
                _ => throw new InvalidOperationException(tool),
            };
            return Task.FromResult((T)result);
        }
    }

    private sealed class ConsistencyEngineeringCaller : IMcpToolCaller
    {
        private readonly string root;
        private readonly Dictionary<string, string> checksums;

        public ConsistencyEngineeringCaller(string root, params (string PlcName, string Checksum)[] checksums)
        {
            this.root = root;
            this.checksums = checksums.ToDictionary(item => item.PlcName, item => item.Checksum);
        }

        public List<string> Calls { get; } = new();
        public string ProjectXml { get; init; } = "<CAEXFile><Device Name=\"PLC_1\" /></CAEXFile>";
        public string SourceXml { get; init; } = "<Document><SW.Blocks.OB ID=\"1\" /></Document>";
        public IReadOnlyDictionary<string, string>? SourceXmlByPlc { get; init; }
        public string ExportMetadataJson { get; init; } = "{}";

        /// <summary>Optional per-PLC safety surface: (isSafetyDevice, readState, fSignature, blockSignatures).</summary>
        public Dictionary<string, (bool IsSafety, string? ReadState, string? FSignature, IReadOnlyList<FBlockSignatureInfo>? Blocks)> Safety { get; } = new();
        public HashSet<string> ChangedEvidencePlcs { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (tool == "get_plc_checksums")
            {
                var plcName = args.GetType().GetProperty("plcName")?.GetValue(args) as string;
                var values = checksums
                    .Where(item => plcName is null || item.Key == plcName)
                    .Select(item =>
                    {
                        Safety.TryGetValue(item.Key, out var safety);
                        return new PlcChecksumInfo
                        {
                            PlcName = item.Key,
                            ProjectIdentity = "project-1",
                            SoftwareChecksum = item.Value,
                            IsSafetyDevice = Safety.ContainsKey(item.Key) ? safety.IsSafety : null,
                            FSignatureReadState = safety.ReadState,
                            FSignature = safety.FSignature,
                            FBlockSignatures = safety.Blocks,
                        };
                    })
                    .ToArray();
                return Task.FromResult((T)(object)values);
            }

            if (tool == "sync_export" || tool == "rebuild_export")
            {
                var outputDir = (string)args.GetType().GetProperty("outputDir")!.GetValue(args)!;
                Directory.CreateDirectory(Path.Combine(outputDir, "Blocks"));
                var plcName = (string)args.GetType().GetProperty("plcName")!.GetValue(args)!;
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "Main.xml"), SourceXmlByPlc?.GetValueOrDefault(plcName) ?? SourceXml);
                File.WriteAllText(Path.Combine(outputDir, "metadata.json"), ExportMetadataJson);
                return Task.FromResult((T)(object)new[]
                {
                    new SyncResult { PlcName = plcName, ExportRoot = outputDir, Status = "updated" },
                });
            }

            if (tool == "compare_source_evidence")
            {
                var plcName = (string)args.GetType().GetProperty("plcName")!.GetValue(args)!;
                var baseline = (SourceEvidenceSnapshot)args.GetType().GetProperty("baseline")!.GetValue(args)!;
                var outputDir = (string)args.GetType().GetProperty("outputDir")!.GetValue(args)!;
                var changed = ChangedEvidencePlcs.Contains(plcName);
                var liveObjects = baseline.Objects.Select(item => new ManagedSourceEvidenceObject
                {
                    Id = item.Id,
                    Name = item.Name,
                    SourcePath = item.SourcePath,
                    Category = item.Category,
                    Kind = item.Kind,
                    ReadState = item.ReadState,
                    Fingerprints = changed && string.Equals(item.Kind, ManagedSourceEvidenceKind.StandardBlock, StringComparison.Ordinal)
                        ? new FingerprintSet { ["Code"] = "changed", ["Comments"] = "same" }
                        : item.Fingerprints,
                    ModifiedTimeStamp = item.ModifiedTimeStamp,
                    FSignature = item.FSignature,
                }).ToArray();
                var candidates = changed
                    ? new[]
                    {
                        new SourceEvidenceCandidate
                        {
                            Id = liveObjects[0].Id,
                            Reason = SourceEvidenceCandidateReason.FingerprintChanged,
                            RequiresXmlExport = true,
                            Live = liveObjects[0],
                            Baseline = baseline.Objects[0],
                        },
                    }
                    : Array.Empty<SourceEvidenceCandidate>();
                var exports = Array.Empty<SourceEvidenceCandidateExport>();
                if (changed)
                {
                    Directory.CreateDirectory(Path.Combine(outputDir, "Blocks"));
                    var path = Path.Combine(outputDir, "Blocks", "Main.xml");
                    File.WriteAllText(path, SourceXml);
                    exports = new[]
                    {
                        new SourceEvidenceCandidateExport
                        {
                            Id = liveObjects[0].Id,
                            SourcePath = liveObjects[0].SourcePath,
                            Export = new ExportResult { BlockName = "Main", Path = path, Success = true },
                        },
                    };
                }
                return Task.FromResult((T)(object)new SourceEvidenceCaptureResult
                {
                    Snapshot = new SourceEvidenceSnapshot
                    {
                        PlcName = plcName,
                        Checksum = new PlcChecksumInfo { PlcName = plcName, SoftwareChecksum = checksums[plcName] },
                        Objects = liveObjects,
                    },
                    Candidates = candidates,
                    CandidateExports = exports,
                });
            }

            if (tool == "capture_source_evidence")
            {
                var plcName = (string)args.GetType().GetProperty("plcName")!.GetValue(args)!;
                return Task.FromResult((T)(object)new SourceEvidenceCaptureResult
                {
                    Snapshot = new SourceEvidenceSnapshot
                    {
                        PlcName = plcName,
                        Checksum = new PlcChecksumInfo { PlcName = plcName, SoftwareChecksum = checksums[plcName] },
                        Objects = new[]
                        {
                            new ManagedSourceEvidenceObject
                            {
                                Id = $"{plcName}-main",
                                Name = "Main",
                                SourcePath = "Blocks/Main.xml",
                                Category = "OB",
                                Kind = ManagedSourceEvidenceKind.StandardBlock,
                                ReadState = ManagedSourceEvidenceReadState.Readable,
                                Fingerprints = new FingerprintSet { ["code"] = checksums[plcName] },
                            },
                        },
                    },
                });
            }

            if (tool == "export_hardware_configuration")
            {
                var outputDir = (string)args.GetType().GetProperty("outputDir")!.GetValue(args)!;
                Directory.CreateDirectory(outputDir);
                var projectAml = Path.Combine(outputDir, "project.aml");
                File.WriteAllText(projectAml, ProjectXml);
                return Task.FromResult((T)(object)new[]
                {
                    new HardwareExportResult
                    {
                        Scope = "project",
                        Success = true,
                        AmlFilePath = projectAml,
                        DurationMs = 1234,
                        NetworkConfigurationDurationMs = 567,
                    },
                });
            }

            throw new InvalidOperationException(tool);
        }
    }

    private sealed class ConsistencyFixture : IDisposable
    {
        private ConsistencyFixture(string root, WorkbenchMetadata workbench, WorktreeMetadata master)
        {
            Root = root;
            Workbench = workbench;
            Master = master;
        }

        public string Root { get; }
        public string Head => "head-1";
        public WorkbenchMetadata Workbench { get; }
        public WorktreeMetadata Master { get; }

        public void WriteSourceManifest(string plcName, string json)
        {
            var deviceId = plcName == "PLC_1" ? "device-1" : "device-2";
            var context = WorkbenchPaths.ResolveDevice("wb-1", Root, "master-1", "master", deviceId, plcName);
            File.WriteAllText(Path.Combine(context.SourceRoot, "metadata.json"), json);
        }

        public static ConsistencyFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "workbench-consistency-tests", Guid.NewGuid().ToString("N"));
            var workbench = new WorkbenchMetadata("1.0", "wb-1", "wb", "now", root, Path.Combine(root, "repository.git"), "project-1", null,
                new[] { new WorkbenchWorktreeRegistration("master-1", "master", "master", "master") });
            var master = new WorktreeMetadata("1.0", "master-1", "wb-1", "master", "master", "now", "head-1", "project-1", null,
                new[] { "device-1", "device-2" }, null);
            var store = new AtomicJsonStore();
            var masterRoot = Path.Combine(root, "worktrees", "master");
            Directory.CreateDirectory(masterRoot);
            store.Write(Path.Combine(masterRoot, "worktree.json"), master);
            foreach (var (id, plcName) in new[] { ("device-1", "PLC_1"), ("device-2", "PLC_2") })
            {
                var context = WorkbenchPaths.ResolveDevice("wb-1", root, "master-1", "master", id, plcName);
                Directory.CreateDirectory(context.SourceRoot);
                Directory.CreateDirectory(context.StagingRoot);
                Directory.CreateDirectory(Path.Combine(context.SourceRoot, "Blocks"));
                File.WriteAllText(Path.Combine(context.SourceRoot, "Blocks", "Main.xml"), "<Document><SW.Blocks.OB ID=\"1\" /></Document>");
                store.Write(Path.Combine(context.DeviceRoot, "device.json"), new DeviceMetadata("1.0", id, "master-1", plcName, "project-1", null, null, null,
                    new KnowledgeState(false, new Dictionary<string, string>(), null), Array.Empty<DeviceImportRecord>()));
            }
            var hardwareRoot = WorkbenchPaths.ResolveHardwareRoot(masterRoot);
            Directory.CreateDirectory(hardwareRoot);
            var hardwareXml = "<CAEXFile><Device Name=\"PLC_1\" /></CAEXFile>";
            File.WriteAllText(Path.Combine(hardwareRoot, "project.aml"), hardwareXml);
            File.WriteAllText(
                Path.Combine(hardwareRoot, "manifest.json"),
                JsonSerializer.Serialize(new { projectContentHash = XmlContentHash.Compute(hardwareXml) }));
            return new ConsistencyFixture(root, workbench, master);
        }

        public ConsistencyValidationEvidence Evidence() => new()
        {
            CommitSha = Head,
            Devices = new[]
            {
                new ConsistencyValidationDevice { DeviceId = "device-1", PlcName = "PLC_1", ProjectChecksum = "one" },
                new ConsistencyValidationDevice { DeviceId = "device-2", PlcName = "PLC_2", ProjectChecksum = "two" },
            },
        };

        public ConsistencyValidationEvidence FingerprintEvidence(string evidenceKind = ManagedSourceEvidenceKind.StandardBlock) => new()
        {
            SchemaVersion = "2.0",
            EvidenceKind = "tia-managed-source",
            CommitSha = Head,
            ManagedSourceConsistent = true,
            Devices = new[]
            {
                FingerprintDevice("device-1", "PLC_1", "one", evidenceKind),
                FingerprintDevice("device-2", "PLC_2", "two", evidenceKind),
            },
        };

        private static ConsistencyValidationDevice FingerprintDevice(string deviceId, string plcName, string checksum, string evidenceKind) => new()
        {
            DeviceId = deviceId,
            PlcName = plcName,
            ProjectChecksum = checksum,
            SourceEvidence = new[]
            {
                new ManagedSourceEvidenceObject
                {
                    Id = $"{deviceId}-main",
                    Name = "Main",
                    SourcePath = "Main",
                    Category = string.Equals(evidenceKind, ManagedSourceEvidenceKind.TagTable, StringComparison.Ordinal) ? "Tags" : "OB",
                    Kind = evidenceKind,
                    ReadState = ManagedSourceEvidenceReadState.Readable,
                    Fingerprints = string.Equals(evidenceKind, ManagedSourceEvidenceKind.StandardBlock, StringComparison.Ordinal)
                        ? new FingerprintSet { ["Code"] = "same", ["Comments"] = "same" }
                        : null,
                    ModifiedTimeStamp = string.Equals(evidenceKind, ManagedSourceEvidenceKind.TagTable, StringComparison.Ordinal)
                        ? DateTimeOffset.UnixEpoch
                        : null,
                },
            },
        };

        /// <summary>Writes master's revision.json with the given aggregated F-signature.</summary>
        public void WriteBaselineFSignature(string? fSignature) =>
            EngineeringStateWriter.Write(
                Path.Combine(Root, "worktrees", "master"),
                EngineeringStateWriter.Create(
                    "^/native/main",
                    1,
                    "PLC_1:one;PLC_2:two",
                    fSignature,
                    EngineeringCompileStatus.Success));

        /// <summary>Writes master's revision.json with per-device safety detail (incl. per-block
        /// signatures), as produced by a current-build savepoint.</summary>
        public void WriteBaselineSafetyDevices(params EngineeringSafetyDevice[] devices) =>
            EngineeringStateWriter.Write(
                Path.Combine(Root, "worktrees", "master"),
                EngineeringStateWriter.Create(
                    "^/native/main",
                    1,
                    "PLC_1:one;PLC_2:two",
                    string.Join(";", devices.Select(device => $"{device.PlcName}:{device.FSignature}")),
                    EngineeringCompileStatus.Success,
                    devices.Select(device => device.ReadState).Contains(FSignatureReadState.ReadFailed)
                        ? FSignatureReadState.ReadFailed
                        : FSignatureReadState.Ok,
                    devices));

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
