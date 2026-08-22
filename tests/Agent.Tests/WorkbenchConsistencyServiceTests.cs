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
        Assert.DoesNotContain("rebuild_export", engineering.Calls);
    }

    [Fact]
    public async Task UnlabeledMasterScansEveryDevice()
    {
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

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
        Assert.Equal(2, engineering.Calls.Count(call => call == "rebuild_export"));
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

        Assert.Equal("tia-sync", evidence.EvidenceKind);
        Assert.False(evidence.MachineValidated);
        Assert.Equal(2, evidence.Devices.Count);
        Assert.Contains("vc_validation_create", versionControl.Calls);
    }

    [Fact]
    public async Task RecordedChecksumsSkipExportWhenHeadRecordedThem()
    {
        WriteRevisionState(fixture.Root, "PLC_1:one;PLC_2:two");
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.Equal(ConsistencyState.Consistent, result.State);
        Assert.True(result.FastGatePassed);
        Assert.DoesNotContain("rebuild_export", engineering.Calls);
    }

    [Fact]
    public async Task PlainCommitOnTopOfTheRecordForcesTheFingerprintScan()
    {
        WriteRevisionState(fixture.Root, "PLC_1:one;PLC_2:two");
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null)
        {
            RevisionRecordSha = "older-savepoint",
        };
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var result = await service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None);

        Assert.False(result.FastGatePassed);
        Assert.Equal(2, engineering.Calls.Count(call => call == "rebuild_export"));
    }

    [Fact]
    public async Task MissingSoftwareChecksumRequiresCompileAndSave()
    {
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", null));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        var exception = await Assert.ThrowsAsync<WorkbenchLifecycleException>(
            () => service.CompareAsync(fixture.Workbench, fixture.Master, CancellationToken.None));

        Assert.Equal("PLC_COMPILE_REQUIRED", exception.Code);
        Assert.DoesNotContain("rebuild_export", engineering.Calls);
    }

    [Fact]
    public async Task CompletingCommitEarnsTiaSyncEvidence()
    {
        var difference = CurrentDifference("device-1", "PLC_1");
        WriteComparison(Comparison("cmp-1", ("device-1", "one"), ("device-2", "two"), difference));
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        await service.TryStampSynchronizedCommitAsync(
            fixture.Workbench,
            fixture.Master,
            "commit-new",
            new[] { difference.RelativePath },
            "Test User <test@example.local>",
            CancellationToken.None);

        Assert.Equal("vc_validation_create", versionControl.Calls.Last());
        var evidence = versionControl.CreatedEvidence!;
        Assert.Equal("tia-sync", evidence.EvidenceKind);
        Assert.Equal("commit-new", evidence.CommitSha);
        Assert.Equal(2, evidence.Devices.Count);
        Assert.Equal("one", evidence.Devices.Single(device => device.PlcName == "PLC_1").ProjectChecksum);
        Assert.Contains(evidence.Devices.Single(device => device.PlcName == "PLC_1").Objects,
            item => item.RelativePath == difference.RelativePath && item.Sha256 == difference.TiaFingerprint);
    }

    [Fact]
    public async Task PartialCommitAdvancesTheLedgerAndTheCompletingCommitEarnsEvidence()
    {
        var first = CurrentDifference("device-1", "PLC_1");
        var second = CurrentDifference("device-2", "PLC_2");
        var path = WriteComparison(Comparison("cmp-1", ("device-1", "one"), ("device-2", "two"), first, second));
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        await service.TryStampSynchronizedCommitAsync(
            fixture.Workbench, fixture.Master, "commit-partial", new[] { first.RelativePath }, "Tester", CancellationToken.None);

        Assert.Null(versionControl.CreatedEvidence);
        var ledger = new AtomicJsonStore().Read<WorkbenchConsistencyResult>(path);
        Assert.Equal(new[] { first.RelativePath }, ledger.CommittedPaths);

        await service.TryStampSynchronizedCommitAsync(
            fixture.Workbench, fixture.Master, "commit-complete", new[] { second.RelativePath }, "Tester", CancellationToken.None);

        Assert.Equal("commit-complete", versionControl.CreatedEvidence?.CommitSha);
    }

    [Fact]
    public async Task StaleTiaChecksumBlocksTheEvidence()
    {
        var difference = CurrentDifference("device-1", "PLC_1");
        WriteComparison(Comparison("cmp-1", ("device-1", "old"), ("device-2", "old"), difference));
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        await service.TryStampSynchronizedCommitAsync(
            fixture.Workbench, fixture.Master, "commit-new", new[] { difference.RelativePath }, "Tester", CancellationToken.None);

        Assert.Null(versionControl.CreatedEvidence);
    }

    [Fact]
    public async Task UnsupportedDifferenceBlocksTheEvidence()
    {
        var difference = CurrentDifference("device-1", "PLC_1") with { Supported = false };
        WriteComparison(Comparison("cmp-1", ("device-1", "one"), ("device-2", "two"), difference));
        var versionControl = new ConsistencyVersionControlCaller(fixture.Head, null);
        var engineering = new ConsistencyEngineeringCaller(fixture.Root, ("PLC_1", "one"), ("PLC_2", "two"));
        var service = new WorkbenchConsistencyService(engineering, versionControl);

        await service.TryStampSynchronizedCommitAsync(
            fixture.Workbench, fixture.Master, "commit-new", new[] { difference.RelativePath }, "Tester", CancellationToken.None);

        Assert.Null(versionControl.CreatedEvidence);
    }

    public void Dispose() => fixture.Dispose();

    private void WriteRevisionState(string root, string aggregateChecksum)
    {
        var masterRoot = Path.Combine(root, "worktrees", "master");
        EngineeringStateWriter.Write(masterRoot, EngineeringStateWriter.Create(
            null,
            null,
            aggregateChecksum,
            null,
            EngineeringCompileStatus.Success));
    }

    private string WriteComparison(WorkbenchConsistencyResult comparison)
    {
        var path = Path.Combine(fixture.Root, ".automation", "comparisons", comparison.ComparisonId + ".json");
        new AtomicJsonStore().Write(path, comparison);
        return path;
    }

    private WorkbenchConsistencyResult Comparison(
        string comparisonId,
        (string DeviceId, string Checksum) deviceOne,
        (string DeviceId, string Checksum) deviceTwo,
        params SourceDifference[] differences) =>
        new(
            comparisonId,
            fixture.Head,
            false,
            ConsistencyState.Different,
            new Dictionary<string, string?>
            {
                [deviceOne.DeviceId] = deviceOne.Checksum,
                [deviceTwo.DeviceId] = deviceTwo.Checksum,
            },
            differences);

    private SourceDifference CurrentDifference(string deviceId, string plcName)
    {
        var context = WorkbenchPaths.ResolveDevice("wb-1", fixture.Root, "master-1", "master", deviceId, plcName);
        var snapshot = new SourceTreeReader().Read(context.SourceRoot).Single();
        var prefix = $"{Path.GetRelativePath(context.WorktreeRoot, context.SourceRoot).Replace('\\', '/')}/";
        return new SourceDifference(
            deviceId,
            plcName,
            prefix + snapshot.RelativePath,
            snapshot.Identity,
            SourceDifferenceKind.Changed,
            "master-fingerprint",
            snapshot.Sha256,
            true);
    }

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
        /// <summary>Sha returned for vc_log calls filtered to revision.json — defaults to HEAD
        /// (HEAD recorded the checksums); set to an older sha to simulate a plain commit on top.</summary>
        public string? RevisionRecordSha { get; set; }
        public TiaSyncEvidence? CreatedEvidence { get; private set; }
        public List<string> Calls { get; } = new();

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (tool == "vc_log")
            {
                var filePath = args.GetType().GetProperty("filePath")?.GetValue(args) as string;
                var sha = filePath is null ? head : RevisionRecordSha ?? head;
                return Task.FromResult((T)(object)new ConsistencyLogResult { Commits = new[] { new ConsistencyCommit { Sha = sha } } });
            }

            if (tool == "vc_validation_create")
            {
                CreatedEvidence = args.GetType().GetProperty("evidence")?.GetValue(args) as TiaSyncEvidence;
                return Task.FromResult((T)(object)(CreatedEvidence ?? new TiaSyncEvidence { EvidenceKind = "tia-sync" }));
            }

            object result = tool switch
            {
                "vc_validation_get" => evidence!,
                "vc_status" => new ConsistencyStatusResult
                {
                    Entries = DirtySource
                        ? new[] { new ConsistencyStatusEntry { FilePath = "devices/PLC_1/source/Blocks/Main.xml" } }
                        : Array.Empty<ConsistencyStatusEntry>(),
                },
                _ => throw new InvalidOperationException(tool),
            };
            return Task.FromResult((T)result);
        }
    }

    private sealed class ConsistencyEngineeringCaller : IMcpToolCaller
    {
        private readonly string root;
        private readonly Dictionary<string, string?> checksums;

        public ConsistencyEngineeringCaller(string root, params (string PlcName, string? Checksum)[] checksums)
        {
            this.root = root;
            this.checksums = checksums.ToDictionary(item => item.PlcName, item => item.Checksum);
        }

        public List<string> Calls { get; } = new();
        public string ProjectXml { get; init; } = "<CAEXFile><Device Name=\"PLC_1\" /></CAEXFile>";

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (tool == "get_plc_checksums")
            {
                var plcName = args.GetType().GetProperty("plcName")?.GetValue(args) as string;
                var values = checksums
                    .Where(item => plcName is null || item.Key == plcName)
                    .Select(item => new PlcChecksumInfo
                    {
                        PlcName = item.Key,
                        ProjectIdentity = "project-1",
                        SoftwareChecksum = item.Value,
                    })
                    .ToArray();
                return Task.FromResult((T)(object)values);
            }

            if (tool == "rebuild_export")
            {
                var outputDir = (string)args.GetType().GetProperty("outputDir")!.GetValue(args)!;
                Directory.CreateDirectory(Path.Combine(outputDir, "Blocks"));
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "Main.xml"), "<Document><SW.Blocks.OB ID=\"1\" /></Document>");
                File.WriteAllText(Path.Combine(outputDir, "metadata.json"), "{}");
                var plcName = (string)args.GetType().GetProperty("plcName")!.GetValue(args)!;
                return Task.FromResult((T)(object)new[]
                {
                    new SyncResult { PlcName = plcName, ExportRoot = outputDir, Status = "updated" },
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

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
