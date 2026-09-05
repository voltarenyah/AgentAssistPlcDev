using Agent.Workbench;
using Contracts.Engineering;
using System.Text.Json;
using Xunit;

namespace Agent.Tests;

public sealed class FeatureImportServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "feature-import-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportResolvesDeviceByMetadataIdWhenFolderUsesPlcName()
    {
        var store = new AtomicJsonStore();
        var workbench = new WorkbenchMetadata(
            WorkbenchSchema.CurrentVersion,
            "wb-1",
            "Line",
            "now",
            root,
            Path.Combine(root, "repository.git"),
            null,
            null,
            new[]
            {
                new WorkbenchWorktreeRegistration("wt-1", "master", "master", "master"),
            });
        var worktree = new WorktreeMetadata(
            WorkbenchSchema.CurrentVersion,
            "wt-1",
            "wb-1",
            "master",
            "master",
            "now",
            null,
            null,
            null,
            new[] { "dev-1" },
            null);
        var context = WorkbenchPaths.ResolveDevice(
            workbench.WorkbenchId,
            workbench.RootPath,
            worktree.WorktreeId,
            "master",
            "dev-1",
            "PLC_1");
        Directory.CreateDirectory(context.SourceRoot);
        Directory.CreateDirectory(context.StagingRoot);
        Directory.CreateDirectory(Path.Combine(context.SourceRoot, "Blocks"));
        File.WriteAllText(Path.Combine(context.SourceRoot, "Blocks", "Main.xml"), "<main />");
        store.Write(Path.Combine(context.DeviceRoot, "device.json"), new DeviceMetadata(
            WorkbenchSchema.CurrentVersion,
            "dev-1",
            "wt-1",
            "PLC_1",
            "PLC_1",
            null,
            null,
            null,
            new KnowledgeState(false, new Dictionary<string, string>(), null),
            Array.Empty<DeviceImportRecord>()));
        store.Write(Path.Combine(context.WorktreeRoot, "worktree.json"), worktree);

        var plan = new FeatureImportPlan(
            "plan-1",
            workbench.WorkbenchId,
            worktree.WorktreeId,
            "feature-sha",
            "master-sha",
            "comparison-1",
            new[]
            {
                new FeatureImportObject(
                    "dev-1",
                    "PLC_1",
                    "devices/PLC_1/source/Blocks/Main.xml",
                    "fingerprint",
                    true,
                    null),
            });
        store.Write(Path.Combine(root, ".automation", "import-plans", "plan-1.json"), plan);

        var engineering = new FakeToolCaller()
            .Respond("import_source_object", new SourceObjectImportResult
            {
                Success = true,
                RelativePath = "Blocks/Main.xml",
                ObjectName = "Main",
            });
        var service = new FeatureImportService(
            engineering,
            new FakeToolCaller(),
            new WorkbenchConsistencyService(new FakeToolCaller(), new FakeToolCaller()),
            store);

        var result = await service.ImportAsync(
            workbench,
            "plan-1",
            new[] { "devices/PLC_1/source/Blocks/Main.xml" });

        Assert.Equal(FeatureImportState.Imported, result.Objects.Single().State);
        Assert.Equal("PLC_1", Property<string>(
            engineering.CallArgs["import_source_object"].Single(),
            "plcName"));
    }

    [Fact]
    public async Task PlanMapsPlcFolderToOpaqueDeviceId()
    {
        var store = new AtomicJsonStore();
        var workbench = new WorkbenchMetadata(
            WorkbenchSchema.CurrentVersion,
            "wb-1",
            "Line",
            "now",
            root,
            Path.Combine(root, "repository.git"),
            null,
            null,
            new[]
            {
                new WorkbenchWorktreeRegistration("master-1", "master", "master", "master"),
                new WorkbenchWorktreeRegistration("feature-1", "feature", "feature", "feature"),
            });
        WriteWorktree(store, workbench, "master-1", "master", "master");
        WriteWorktree(store, workbench, "feature-1", "feature", "feature");

        var versionControl = new FeaturePlanVersionControlCaller();
        var engineering = new FakeToolCaller()
            .Respond("get_plc_checksums", new[]
            {
                new PlcChecksumInfo
                {
                    PlcName = "PLC_1",
                    ProjectIdentity = "project-1",
                    SoftwareChecksum = "checksum-1",
                },
            })
            .Respond("export_hardware_configuration", args =>
            {
                var outputDir = (string)args.GetType().GetProperty("outputDir")!.GetValue(args)!;
                Directory.CreateDirectory(outputDir);
                var projectAml = Path.Combine(outputDir, "project.aml");
                File.WriteAllText(projectAml, "<CAEXFile />");
                return new[]
                {
                    new HardwareExportResult
                    {
                        Scope = "project",
                        Success = true,
                        AmlFilePath = projectAml,
                    },
                };
            });
        var service = new FeatureImportService(
            engineering,
            versionControl,
            new WorkbenchConsistencyService(engineering, versionControl, store: store),
            store);

        var plan = await service.PlanAsync(
            workbench,
            store.Read<WorktreeMetadata>(Path.Combine(root, "worktrees", "feature", "worktree.json")));

        Assert.Equal("dev-1", plan.Objects.Single().DeviceId);
        Assert.Equal("PLC_1", plan.Objects.Single().PlcName);
    }

    [Fact]
    public async Task ValidatedMergeUsesPlcFolderWhenMatchingCandidateObjects()
    {
        var store = new AtomicJsonStore();
        var workbench = new WorkbenchMetadata(
            WorkbenchSchema.CurrentVersion,
            "wb-1",
            "Line",
            "now",
            root,
            Path.Combine(root, "repository.git"),
            null,
            null,
            new[]
            {
                new WorkbenchWorktreeRegistration("master-1", "master", "master", "master"),
                new WorkbenchWorktreeRegistration("feature-1", "feature", "feature", "feature"),
            });
        WriteWorktree(store, workbench, "master-1", "master", "master");
        WriteWorktree(store, workbench, "feature-1", "feature", "feature");
        var masterContext = WorkbenchPaths.ResolveDevice(
            workbench.WorkbenchId,
            workbench.RootPath,
            "master-1",
            "master",
            "dev-1",
            "PLC_1");
        var fingerprint = new SourceTreeReader().Read(masterContext.SourceRoot).Single().Sha256;
        var versionControl = new ValidatedMergeVersionControlCaller(fingerprint);
        var engineering = new ValidatedMergeEngineeringCaller();
        var service = new ValidatedMergeCoordinator(engineering, versionControl, store);
        var session = new FeatureImportSession(
            "session-1",
            "plan-1",
            "feature-sha",
            "master-sha",
            "now",
            new[]
            {
                new FeatureImportOutcome(
                    "dev-1",
                    "devices/PLC_1/source/Blocks/Main.xml",
                    FeatureImportState.Imported,
                    null,
                    Array.Empty<string>()),
            });

        var result = await service.ValidateAsync(
            workbench,
            store.Read<WorktreeMetadata>(Path.Combine(root, "worktrees", "feature", "worktree.json")),
            session,
            new ValidateFeatureMergeRequest("wb-1", "feature-1", "session-1", true, "Test User"));

        Assert.Equal(ValidatedMergeState.Ready, result.State);
        Assert.Single(result.Devices);
        Assert.Equal("PLC_1", result.Devices.Single().PlcName);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static T Property<T>(object value, string name) =>
        (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    private void WriteWorktree(
        AtomicJsonStore store,
        WorkbenchMetadata workbench,
        string worktreeId,
        string name,
        string branch)
    {
        var context = WorkbenchPaths.ResolveDevice(
            workbench.WorkbenchId,
            workbench.RootPath,
            worktreeId,
            name,
            "dev-1",
            "PLC_1");
        Directory.CreateDirectory(context.SourceRoot);
        Directory.CreateDirectory(context.StagingRoot);
        Directory.CreateDirectory(Path.Combine(context.SourceRoot, "Blocks"));
        File.WriteAllText(
            Path.Combine(context.SourceRoot, "Blocks", "Main.xml"),
            "<main />");
        store.Write(
            Path.Combine(context.WorktreeRoot, "worktree.json"),
            new WorktreeMetadata(
                WorkbenchSchema.CurrentVersion,
                worktreeId,
                workbench.WorkbenchId,
                name,
                branch,
                "now",
                null,
                null,
                null,
                new[] { "dev-1" },
                null));
        store.Write(
            Path.Combine(context.DeviceRoot, "device.json"),
            new DeviceMetadata(
                WorkbenchSchema.CurrentVersion,
                "dev-1",
                worktreeId,
                "PLC_1",
                "PLC_1",
                null,
                null,
                null,
                new KnowledgeState(false, new Dictionary<string, string>(), null),
                Array.Empty<DeviceImportRecord>()));
        var hardwareRoot = WorkbenchPaths.ResolveHardwareRoot(context.WorktreeRoot);
        Directory.CreateDirectory(hardwareRoot);
        var hardwareXml = "<CAEXFile />";
        File.WriteAllText(Path.Combine(hardwareRoot, "project.aml"), hardwareXml);
        File.WriteAllText(
            Path.Combine(hardwareRoot, "manifest.json"),
            JsonSerializer.Serialize(new { projectContentHash = XmlContentHash.Compute(hardwareXml) }));
    }

    private sealed class FeaturePlanVersionControlCaller : FakeToolCaller
    {
        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            object result = tool switch
            {
                "vc_status" => new ConsistencyStatusResult(),
                "vc_log" => new ConsistencyLogResult
                {
                    Commits = new[] { new ConsistencyCommit { Sha = "head-1" } },
                },
                "vc_validation_get" => new ConsistencyValidationEvidence
                {
                    CommitSha = "head-1",
                    Devices = new[]
                    {
                        new ConsistencyValidationDevice
                        {
                            DeviceId = "dev-1",
                            PlcName = "PLC_1",
                            ProjectChecksum = "checksum-1",
                        },
                    },
                },
                "vc_untrackable_change_get" => new { untrackableChange = false },
                "vc_merge_preview" => new
                {
                    targetBranch = "master",
                    sourceBranch = "feature",
                    mergeBaseSha = "base-1",
                    targetSha = "head-1",
                    sourceSha = "head-1",
                    candidateTreeSha = "candidate-1",
                    hasConflicts = false,
                    conflictPaths = Array.Empty<string>(),
                    featurePaths = new[] { "devices/PLC_1/source/Blocks/Main.xml" },
                    objects = new[]
                    {
                        new
                        {
                            filePath = "devices/PLC_1/source/Blocks/Main.xml",
                            sha256 = "feature-fingerprint",
                            length = 10L,
                        },
                    },
                },
                _ => throw new InvalidOperationException(tool),
            };
            var json = JsonSerializer.Serialize(result);
            return Task.FromResult(JsonSerializer.Deserialize<T>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
        }
    }

    private sealed class ValidatedMergeVersionControlCaller(string fingerprint) : FakeToolCaller
    {
        private int logCalls;

        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            object result = tool switch
            {
                "vc_merge_preview" => new
                {
                    targetBranch = "master",
                    sourceBranch = "feature",
                    mergeBaseSha = "base-1",
                    targetSha = "master-sha",
                    sourceSha = "feature-sha",
                    candidateTreeSha = "candidate-1",
                    hasConflicts = false,
                    conflictPaths = Array.Empty<string>(),
                    featurePaths = new[] { "devices/PLC_1/source/Blocks/Main.xml" },
                    objects = new[]
                    {
                        new
                        {
                            filePath = "devices/PLC_1/source/Blocks/Main.xml",
                            sha256 = fingerprint,
                            length = 10L,
                        },
                    },
                },
                "vc_log" => new ConsistencyLogResult
                {
                    Commits = new[]
                    {
                        new ConsistencyCommit
                        {
                            Sha = Interlocked.Increment(ref logCalls) % 2 == 1 ? "feature-sha" : "master-sha",
                        },
                    },
                },
                _ => throw new InvalidOperationException(tool),
            };
            return Task.FromResult(JsonSerializer.Deserialize<T>(
                JsonSerializer.Serialize(result),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
        }
    }

    private sealed class ValidatedMergeEngineeringCaller : FakeToolCaller
    {
        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (tool == "compile_plc")
                return Task.FromResult((T)(object)new CompileResult { State = "success" });

            if (tool == "get_plc_checksums")
            {
                return Task.FromResult((T)(object)new[]
                {
                    new PlcChecksumInfo
                    {
                        PlcName = "PLC_1",
                        ProjectIdentity = "project-1",
                        SoftwareChecksum = "checksum-1",
                    },
                });
            }

            if (tool == "rebuild_export" || tool == "sync_export")
            {
                var output = Property<string>(args, "outputDir");
                Directory.CreateDirectory(Path.Combine(output, "Blocks"));
                File.WriteAllText(Path.Combine(output, "Blocks", "Main.xml"), "<main />");
                File.WriteAllText(Path.Combine(output, "metadata.json"), "{}");
                return Task.FromResult((T)(object)new[]
                {
                    new SyncResult { PlcName = "PLC_1", ExportRoot = output },
                });
            }

            throw new InvalidOperationException(tool);
        }
    }
}
