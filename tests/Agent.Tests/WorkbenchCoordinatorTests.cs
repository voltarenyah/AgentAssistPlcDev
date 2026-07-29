using Agent.Mcp;
using Agent.Workbench;
using Contracts.Engineering;
using Contracts.Knowledge;
using System.Text.Json;
using Xunit;

namespace Agent.Tests;

public sealed class WorkbenchCoordinatorTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"coordinator-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task OpenProjectInTiaUsesRegisteredProjectWithUi()
    {
        var fixture = Fixture.Create(root, sourceProjectPath: @"C:\Projects\Line.ap17");
        var engineering = new FakeToolCaller().Respond("connect", new { connected = true });
        var coordinator = Create(fixture, engineering: engineering);

        await coordinator.OpenProjectInTiaAsync(fixture.Context, CancellationToken.None);

        var args = engineering.CallArgs["connect"].Single();
        Assert.Equal(@"C:\Projects\Line.ap17", Property<string>(args, "projectPath"));
        Assert.True(Property<bool>(args, "withUI"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OpenProjectInTiaRejectsMissingRegisteredProjectBeforeEngineeringCall(
        string? sourceProjectPath)
    {
        var fixture = Fixture.Create(root, sourceProjectPath: sourceProjectPath);
        var engineering = new FakeToolCaller();
        var coordinator = Create(fixture, engineering: engineering);

        var error = await Assert.ThrowsAsync<WorkbenchCatalogException>(
            () => coordinator.OpenProjectInTiaAsync(fixture.Context, CancellationToken.None));

        Assert.Equal("ENGINEERING_PROJECT_PATH_MISSING", error.Code);
        Assert.Empty(engineering.CallArgs);
    }

    [Fact]
    public async Task OpenProjectInTiaWaitsForActiveStagedExportEngineeringSequence()
    {
        var fixture = Fixture.Create(root, sourceProjectPath: @"C:\Projects\Line.ap17");
        var engineering = new BlockingEngineeringCaller();
        var coordinator = Create(fixture, engineering: engineering);

        var staging = coordinator.StageRefreshAsync(
            fixture.Context,
            CancellationToken.None);
        await engineering.ExportEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var opening = coordinator.OpenProjectInTiaAsync(
            fixture.Context,
            CancellationToken.None);

        await Task.Yield();
        Assert.False(opening.IsCompleted);
        Assert.Equal(["rebuild_export"], engineering.Calls);

        engineering.ReleaseExport.SetResult();
        await Task.WhenAll(staging, opening);

        Assert.Equal(["rebuild_export", "connect"], engineering.Calls);
    }

    [Fact]
    public async Task OpenProjectInTiaWaitsForWorkbenchDiscoverySequence()
    {
        var fixture = Fixture.Create(root, sourceProjectPath: @"C:\Projects\Existing.ap17");
        var engineering = new BlockingDiscoveryEngineeringCaller();
        var versionControl = new FakeToolCaller().Respond("vc_init_shared", new object());
        var coordinator = Create(
            fixture,
            engineering: engineering,
            versionControl: versionControl);
        var createRoot = Path.Combine(root, "created");

        var creating = coordinator.CreateWorkbenchAsync(
            new CreateWorkbenchRequest("Created", createRoot, 42, @"C:\Projects\Created.ap17"));
        await engineering.ProjectInfoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var opening = coordinator.OpenProjectInTiaAsync(
            fixture.Context,
            CancellationToken.None);

        await Task.Yield();
        Assert.False(opening.IsCompleted);
        Assert.Equal(["connect", "get_project_info"], engineering.Calls);

        engineering.ReleaseProjectInfo.SetResult();
        await Task.WhenAll(creating, opening);
        Assert.Equal(["connect", "get_project_info", "connect"], engineering.Calls);
    }

    [Fact]
    public async Task CancelledWorkbenchDiscoveryReleasesEngineeringSession()
    {
        var fixture = Fixture.Create(root, sourceProjectPath: @"C:\Projects\Existing.ap17");
        var engineering = new BlockingDiscoveryEngineeringCaller();
        var versionControl = new FakeToolCaller().Respond("vc_init_shared", new object());
        var coordinator = Create(
            fixture,
            engineering: engineering,
            versionControl: versionControl);
        using var cancellation = new CancellationTokenSource();
        var creating = coordinator.CreateWorkbenchAsync(
            new CreateWorkbenchRequest(
                "Cancelled",
                Path.Combine(root, "cancelled"),
                42,
                @"C:\Projects\Cancelled.ap17"),
            cancellation.Token);
        await engineering.ProjectInfoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creating);
        await coordinator.OpenProjectInTiaAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(["connect", "get_project_info", "connect"], engineering.Calls);
    }

    [Fact]
    public async Task OpenProjectInTiaWaitsForFullImportAndCompileSequence()
    {
        var fixture = Fixture.Create(root, sourceProjectPath: @"C:\Projects\Line.ap17");
        fixture.WriteModified("Blocks/A.xml", "<modified />");
        var engineering = new BlockingImportEngineeringCaller();
        var coordinator = Create(fixture, engineering: engineering);

        var importing = coordinator.ImportModifiedAsync(
            fixture.Context,
            "Blocks/A.xml",
            CancellationToken.None);
        await engineering.ImportEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var opening = coordinator.OpenProjectInTiaAsync(
            fixture.Context,
            CancellationToken.None);

        await Task.Yield();
        Assert.False(opening.IsCompleted);
        Assert.Equal(["import_block"], engineering.Calls);

        engineering.ReleaseImport.SetResult();
        await Task.WhenAll(importing, opening);
        Assert.Equal(["import_block", "compile_block", "connect"], engineering.Calls);
    }

    [Fact]
    public async Task CreateInitializesGitThenConnectsAndDiscoversDevicesBeforeMetadata()
    {
        var calls = new List<string>();
        var versionControl = Caller(calls).Respond("vc_init_shared", new object());
        var engineering = Caller(calls)
            .Respond("connect", new object())
            .Respond("get_project_info", new ProjectInfo
            {
                Name = "Line",
                Path = @"C:\Projects\Line.ap17",
                PlcDevices = new[] { "PLC_1", "PLC_2" },
            });
        var catalog = new WorkbenchCatalog(
            new AtomicJsonStore(),
            Path.Combine(root, "catalog"));
        var coordinator = new WorkbenchCoordinator(
            engineering,
            new FakeToolCaller(),
            versionControl,
            catalog,
            new AtomicJsonStore(),
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));

        var progress = new RecordingProgress();

        var result = await coordinator.CreateWorkbenchAsync(
            new CreateWorkbenchRequest(
                "Line",
                Path.Combine(root, "Line"),
                42,
                @"C:\Projects\Line.ap17"),
            progress: progress);

        Assert.Equal(
            new[]
            {
                "Preparing workbench storage...",
                "Initializing Git repository...",
                "Attaching to TIA Portal...",
                "Discovering PLC devices...",
                "Creating device folders...",
            },
            progress.Messages);
        Assert.Equal(
            new[]
            {
                "version:vc_init_shared",
                "engineering:connect",
                "engineering:get_project_info",
            },
            calls);
        Assert.Equal(
            42,
            Property<int>(engineering.CallArgs["connect"].Single(), "sessionId"));
        Assert.Equal(2, result.Devices.Count);
        Assert.All(result.Devices, device =>
        {
            var context = catalog.ResolveDevice(result.Workbench, result.Worktree, device);
            Assert.True(File.Exists(Path.Combine(context.DeviceRoot, "device.json")));
        });
        Assert.True(File.Exists(Path.Combine(
            result.Workbench.RootPath, "worktrees", "master", "worktree.json")));
    }

    [Fact]
    public async Task CreateRollsBackWorkbenchArtifactsWhenGitInitializationFails()
    {
        var workbenchRoot = Path.Combine(root, "FailedCreate");
        var catalog = new WorkbenchCatalog(
            new AtomicJsonStore(),
            Path.Combine(root, "catalog"));
        var coordinator = new WorkbenchCoordinator(
            new FakeToolCaller(),
            new FakeToolCaller(),
            new FakeToolCaller().Fail("vc_init_shared", "GIT_TIMEOUT", "Git timed out."),
            catalog,
            new AtomicJsonStore(),
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));

        await Assert.ThrowsAsync<ToolCallException>(() =>
            coordinator.CreateWorkbenchAsync(
                new CreateWorkbenchRequest(
                    "FailedCreate",
                    workbenchRoot,
                    42,
                    @"C:\Projects\Line.ap17")));

        Assert.False(File.Exists(Path.Combine(workbenchRoot, "workbench.json")));
        Assert.False(Directory.Exists(Path.Combine(workbenchRoot, "repository.git")));
        Assert.False(Directory.Exists(Path.Combine(workbenchRoot, "worktrees")));
    }

    [Fact]
    public async Task StageRefreshExportsSelectedPlcOnlyToStaging()
    {
        var fixture = Fixture.Create(root);
        var calls = new List<string>();
        var engineering = new MutatingExportCaller(new[]
        {
            new SyncResult { PlcName = "PLC_1", ExportRoot = fixture.Context.StagingRoot },
        }, calls);
        var coordinator = Create(fixture, engineering: engineering);
        var metadataBefore = File.ReadAllBytes(
            Path.Combine(fixture.Context.DeviceRoot, "device.json"));

        var progress = new RecordingProgress();

        await coordinator.StageRefreshAsync(fixture.Context, CancellationToken.None, progress);

        Assert.Equal(new[] { "engineering:rebuild_export" }, calls);
        Assert.Contains("Preparing export staging area...", progress.Messages);
        Assert.Contains("Exporting PLC source...", progress.Messages);
        Assert.Contains("Writing export metadata...", progress.Messages);
        Assert.Contains("Preparing refresh preview...", progress.Messages);
        var args = engineering.CallArgs["rebuild_export"].Single();
        Assert.StartsWith(fixture.Context.DeviceRoot, Property<string>(args, "outputDir"));
        Assert.Equal("PLC_1", Property<string>(args, "plcName"));
        Assert.Equal(
            metadataBefore,
            File.ReadAllBytes(Path.Combine(fixture.Context.DeviceRoot, "device.json")));
    }

    [Fact]
    public async Task IncompleteStagePreservesPreviousStagingAndFailsExplicitly()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteStaging("sentinel.txt", "previous");
        var engineering = new MutatingExportCaller(new[]
        {
            new SyncResult
            {
                PlcName = "PLC_1",
                Failed = new[] { new SyncChange { Name = "A", Reason = "export failed" } },
            },
        });
        var coordinator = Create(fixture, engineering: engineering);

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(
            () => coordinator.StageRefreshAsync(fixture.Context, CancellationToken.None));

        Assert.Equal("DEVICE_EXPORT_INCOMPLETE", error.Code);
        Assert.Equal("previous", File.ReadAllText(
            Path.Combine(fixture.Context.StagingRoot, "sentinel.txt")));
    }

    [Fact]
    public async Task StageRefreshForwardsMcpProgressMessages()
    {
        var fixture = Fixture.Create(root);
        var engineering = new ProgressExportCaller(new[]
        {
            "Exporting block Main_OB1...",
            "Exporting tag table MachineTags...",
        });
        var coordinator = Create(fixture, engineering: engineering);
        var progress = new RecordingProgress();

        await coordinator.StageRefreshAsync(fixture.Context, CancellationToken.None, progress);

        Assert.Contains("Exporting block Main_OB1...", progress.Messages);
        Assert.Contains("Exporting tag table MachineTags...", progress.Messages);
    }

    [Fact]
    public async Task FailedReplacementAndRestorePreservesBackupForRecovery()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteStaging("sentinel.txt", "previous");
        var engineering = new MutatingExportCaller(new[]
        {
            new SyncResult { PlcName = "PLC_1" },
        });
        var files = new FailSecondAndThirdMoveOperations();
        var stager = new SafeDeviceExportStager(
            engineering,
            new DeviceOperationLock(),
            files);

        var error = await Assert.ThrowsAsync<AggregateException>(
            () => stager.StageAsync(fixture.Context, "PLC_1"));

        Assert.Contains("backup was preserved", error.Message, StringComparison.OrdinalIgnoreCase);
        var backup = Assert.Single(Directory.EnumerateDirectories(
            fixture.Context.DeviceRoot,
            ".staging-*.backup"));
        Assert.Equal("previous", File.ReadAllText(Path.Combine(backup, "sentinel.txt")));
    }

    [Fact]
    public async Task ApplyRefreshReconcilesThenStagesExactPathsAndCommits()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteBaseline("Blocks/A.xml", "<old />");
        fixture.WriteStaging("Blocks/A.xml", "<new />");
        fixture.WriteStaging("Blocks/B.xml", "<added />");
        fixture.WriteManifests("Blocks/A.xml", "Blocks/B.xml");
        var calls = new List<string>();
        var versionControl = Caller(calls)
            .Respond("vc_add", new AddResult())
            .Respond("vc_commit", new CoordinatorGitCommitResult { Sha = "abc123" });
        var coordinator = Create(fixture, versionControl: versionControl);
        var preview = coordinator.PreviewRefresh(fixture.Context);

        var result = await coordinator.ApplyRefreshAsync(
            fixture.Context,
            new ApprovedReconciliation(preview, new HashSet<string>()),
            CancellationToken.None);

        Assert.Equal(RefreshApplyState.Committed, result.State);
        Assert.Equal(new[] { "version:vc_add", "version:vc_commit" }, calls);
        var expected = new[]
        {
            Relative(fixture.Context, "device.json"),
            Relative(fixture.Context, "exported-source/Blocks/A.xml"),
            Relative(fixture.Context, "exported-source/Blocks/B.xml"),
            Relative(fixture.Context, "exported-source/metadata.json"),
        };
        Assert.Equal(expected.Order(), result.ChangedPaths.Order());
        Assert.Equal(expected.Order(), Property<string[]>(versionControl.CallArgs["vc_add"].Single(), "paths").Order());
        Assert.Equal("abc123", ReadDevice(fixture).LastReconciliationCommit);
    }

    [Fact]
    public async Task RejectedRefreshNeverStagesOrCommits()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteStaging("Blocks/A.xml", "<new />");
        fixture.WriteManifests("Blocks/A.xml");
        var calls = new List<string>();
        var versionControl = Caller(calls);
        var coordinator = Create(fixture, versionControl: versionControl);

        var result = await coordinator.ApplyRefreshAsync(
            fixture.Context,
            ApprovedReconciliation.Rejected(coordinator.PreviewRefresh(fixture.Context)),
            CancellationToken.None);

        Assert.Equal(RefreshApplyState.Rejected, result.State);
        Assert.Empty(calls);
        Assert.False(File.Exists(Path.Combine(fixture.Context.ExportedSourceRoot, "Blocks", "A.xml")));
    }

    [Fact]
    public async Task ApprovedNoOpLeavesMetadataCleanAndDoesNotCallGit()
    {
        var fixture = Fixture.Create(root, knowledgeStale: false);
        fixture.WriteBaseline("Blocks/A.xml", "<same />");
        fixture.WriteStaging("Blocks/A.xml", "<same />");
        fixture.WriteManifests("Blocks/A.xml");
        // Make the two manifests byte-identical so reconciliation is a true no-op.
        File.Copy(
            Path.Combine(fixture.Context.ExportedSourceRoot, "metadata.json"),
            Path.Combine(fixture.Context.StagingRoot, "metadata.json"),
            overwrite: true);
        var before = File.ReadAllBytes(Path.Combine(fixture.Context.DeviceRoot, "device.json"));
        var version = new FakeToolCaller();
        var coordinator = Create(fixture, versionControl: version);

        var result = await coordinator.ApplyRefreshAsync(
            fixture.Context,
            new ApprovedReconciliation(
                coordinator.PreviewRefresh(fixture.Context),
                new HashSet<string>()),
            CancellationToken.None);

        Assert.Empty(result.ChangedPaths);
        Assert.Empty(version.Calls);
        Assert.Equal(
            before,
            File.ReadAllBytes(Path.Combine(fixture.Context.DeviceRoot, "device.json")));
    }

    [Fact]
    public async Task CommitFailureReportsFilesUpdatedWithoutRollingThemBack()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteBaseline("Blocks/A.xml", "<old />");
        fixture.WriteStaging("Blocks/A.xml", "<new />");
        fixture.WriteManifests("Blocks/A.xml");
        var calls = new List<string>();
        var versionControl = Caller(calls)
            .Respond("vc_add", new AddResult())
            .Fail("vc_commit", "GIT_ERROR", "identity unavailable");
        var coordinator = Create(fixture, versionControl: versionControl);
        var preview = coordinator.PreviewRefresh(fixture.Context);

        var result = await coordinator.ApplyRefreshAsync(
            fixture.Context,
            new ApprovedReconciliation(preview, new HashSet<string>()),
            CancellationToken.None);

        Assert.Equal(RefreshApplyState.FilesUpdatedCommitFailed, result.State);
        Assert.Equal("<new />", File.ReadAllText(
            Path.Combine(fixture.Context.ExportedSourceRoot, "Blocks", "A.xml")));
        Assert.Equal(new[] { "version:vc_add", "version:vc_commit" }, calls);
        Assert.Null(ReadDevice(fixture).LastReconciliationCommit);
        Assert.True(ReadDevice(fixture).Knowledge.Stale);
        Assert.True(ReadDevice(fixture).Knowledge.BaselineStale);
    }

    [Fact]
    public async Task KnowledgeUpdateUsesDeviceDatabaseAndPersistsAppliedHashes()
    {
        var fixture = Fixture.Create(root, knowledgeStale: true);
        File.WriteAllText(fixture.Context.KnowledgeDbPath, "exists");
        fixture.WriteModified("Blocks/A.xml", "<modified />");
        var calls = new List<string>();
        var knowledge = Caller(calls).Respond(
            "update_components",
            new KnowledgeUpdateResult(
                fixture.Context.KnowledgeDbPath,
                new[] { "block:A" },
                new Dictionary<string, string> { ["Blocks/A.xml"] = "hash-a" },
                Array.Empty<string>()));
        var coordinator = Create(fixture, knowledge: knowledge);

        var result = await coordinator.UpdateKnowledgeAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(new[] { "knowledge:update_components" }, calls);
        var args = knowledge.CallArgs["update_components"].Single();
        Assert.Equal(fixture.Context.ExportedSourceRoot, Property<string>(args, "exportedSourceRoot"));
        Assert.Equal(fixture.Context.ModifiedSourceRoot, Property<string>(args, "modifiedSourceRoot"));
        Assert.Equal(fixture.Context.KnowledgeDbPath, Property<string>(args, "dbPath"));
        Assert.Equal(new[] { "Blocks/A.xml" }, Property<string[]>(args, "relativePaths"));
        Assert.Equal("hash-a", ReadDevice(fixture).Knowledge.AppliedOverlayHashes["Blocks/A.xml"]);
        Assert.False(ReadDevice(fixture).Knowledge.Stale);
        Assert.Equal(fixture.Context.KnowledgeDbPath, result.DbPath);
    }

    [Fact]
    public async Task MissingDatabaseUsesFullIngestInsteadOfPartialUpdate()
    {
        var fixture = Fixture.Create(root, knowledgeStale: true);
        fixture.WriteModified("Blocks/A.xml", "<modified />");
        var calls = new List<string>();
        var knowledge = Caller(calls).Respond(
            "ingest_source",
            new IngestResult { DbPath = fixture.Context.KnowledgeDbPath });
        var coordinator = Create(fixture, knowledge: knowledge);

        await coordinator.UpdateKnowledgeAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(new[] { "knowledge:ingest_source" }, calls);
        Assert.False(ReadDevice(fixture).Knowledge.Stale);
    }

    [Fact]
    public async Task ExplicitRebuildAlwaysUsesFullIngestEvenWhenDatabaseIsCurrent()
    {
        var fixture = Fixture.Create(root, knowledgeStale: false);
        File.WriteAllText(fixture.Context.KnowledgeDbPath, "exists");
        var knowledge = new FakeToolCaller()
            .Respond("ingest_source", new IngestResult { DbPath = fixture.Context.KnowledgeDbPath });
        var coordinator = Create(fixture, knowledge: knowledge);

        await coordinator.RebuildKnowledgeAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(new[] { "ingest_source" }, knowledge.Calls);
        Assert.False(ReadDevice(fixture).Knowledge.Stale);
        Assert.False(ReadDevice(fixture).Knowledge.BaselineStale);
    }

    [Fact]
    public async Task OverlayStaleWithNoChangedOverlaySkipsPartialTool()
    {
        var fixture = Fixture.Create(root, knowledgeStale: true);
        File.WriteAllText(fixture.Context.KnowledgeDbPath, "exists");
        var knowledge = new FakeToolCaller();
        var coordinator = Create(fixture, knowledge: knowledge);

        var result = await coordinator.UpdateKnowledgeAsync(
            fixture.Context,
            CancellationToken.None);

        Assert.Empty(knowledge.Calls);
        Assert.Empty(result.UpdatedComponents);
        Assert.False(ReadDevice(fixture).Knowledge.Stale);
    }

    [Fact]
    public async Task BaselineReconciliationForcesFullKnowledgeRebuild()
    {
        var fixture = Fixture.Create(root, knowledgeStale: false);
        File.WriteAllText(fixture.Context.KnowledgeDbPath, "exists");
        fixture.WriteStaging("Blocks/A.xml", "<new />");
        fixture.WriteManifests("Blocks/A.xml");
        var version = new FakeToolCaller()
            .Respond("vc_add", new AddResult())
            .Respond("vc_commit", new CoordinatorGitCommitResult { Sha = "abc" });
        var knowledge = new FakeToolCaller()
            .Respond("ingest_source", new IngestResult { DbPath = fixture.Context.KnowledgeDbPath });
        var coordinator = Create(fixture, knowledge: knowledge, versionControl: version);
        var preview = coordinator.PreviewRefresh(fixture.Context);
        await coordinator.ApplyRefreshAsync(
            fixture.Context,
            new ApprovedReconciliation(preview, new HashSet<string>()),
            CancellationToken.None);

        await coordinator.UpdateKnowledgeAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(new[] { "ingest_source" }, knowledge.Calls);
        Assert.False(ReadDevice(fixture).Knowledge.BaselineStale);
    }

    [Fact]
    public async Task ImportUsesOverlayCompilesAndRetainsOverlay()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteModified("Blocks/A.xml", "<modified />");
        var calls = new List<string>();
        var engineering = Caller(calls)
            .Respond("import_block", new ImportResult
            {
                BlockName = "A",
                Success = true,
                ImportedAt = new DateTime(2026, 7, 27, 1, 2, 3, DateTimeKind.Utc),
            })
            .Respond("compile_block", new CompileResult { BlockName = "A", State = "success" });
        var coordinator = Create(fixture, engineering: engineering);

        var result = await coordinator.ImportModifiedAsync(
            fixture.Context,
            "Blocks/A.xml",
            CancellationToken.None);

        Assert.Equal(new[] { "engineering:import_block", "engineering:compile_block" }, calls);
        var overlay = Path.Combine(fixture.Context.ModifiedSourceRoot, "Blocks", "A.xml");
        Assert.Equal(overlay, Property<string>(
            engineering.CallArgs["import_block"].Single(), "xmlFilePath"));
        Assert.True(File.Exists(overlay));
        Assert.Equal("success", result.CompileState);
        Assert.Single(ReadDevice(fixture).Imports);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private WorkbenchCoordinator Create(
        Fixture fixture,
        FakeToolCaller? engineering = null,
        FakeToolCaller? knowledge = null,
        FakeToolCaller? versionControl = null) =>
        new(
            engineering ?? new FakeToolCaller(),
            knowledge ?? new FakeToolCaller(),
            versionControl ?? new FakeToolCaller(),
            new WorkbenchCatalog(new AtomicJsonStore(), Path.Combine(root, "catalog")),
            new AtomicJsonStore(),
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));

    private static FakeToolCaller Caller(List<string> calls) => new RecordingCaller(calls);

    private static T Property<T>(object value, string name) =>
        (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    private static string Relative(DeviceContext context, string relative) =>
        Path.GetRelativePath(context.WorktreeRoot, Path.Combine(context.DeviceRoot, relative))
            .Replace('\\', '/');

    private static DeviceMetadata ReadDevice(Fixture fixture) =>
        new AtomicJsonStore().Read<DeviceMetadata>(
            Path.Combine(fixture.Context.DeviceRoot, "device.json"));

    private sealed class RecordingCaller : FakeToolCaller
    {
        private readonly List<string> order;

        public RecordingCaller(List<string> order) => this.order = order;

        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            order.Add($"{Prefix(tool)}:{tool}");
            return base.CallAsync<T>(tool, args, cancellationToken);
        }

        private static string Prefix(string tool) =>
            tool.StartsWith("vc_", StringComparison.Ordinal) ? "version"
            : tool is "update_components" or "ingest_source" ? "knowledge"
            : "engineering";
    }

    private sealed class AddResult { }

    private sealed class RecordingProgress : IOperationProgress
    {
        public List<string> Messages { get; } = [];
        public void Report(string message) => Messages.Add(message);
    }

    private sealed class MutatingExportCaller : FakeToolCaller
    {
        private readonly SyncResult[] result;

        private readonly List<string>? order;

        public MutatingExportCaller(SyncResult[] result, List<string>? order = null)
        {
            this.result = result;
            this.order = order;
        }

        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            CallArgs[tool] = new List<object> { args };
            order?.Add($"engineering:{tool}");
            var output = Property<string>(args, "outputDir");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "partial.txt"), "partial");
            File.WriteAllText(Path.Combine(output, "metadata.json"), "{}");
            return Task.FromResult((T)(object)result);
        }
    }

    private sealed class ProgressExportCaller(string[] progressMessages) : FakeToolCaller, IProgressMcpToolCaller
    {
        public Task<T> CallAsync<T>(
            string tool,
            object args,
            IProgress<ModelContextProtocol.ProgressNotificationValue>? progress,
            CancellationToken cancellationToken = default)
        {
            foreach (var message in progressMessages)
            {
                progress?.Report(new ModelContextProtocol.ProgressNotificationValue
                {
                    Progress = 0,
                    Message = message,
                });
            }

            Calls.Add(tool);
            CallArgs[tool] = new List<object> { args };
            var output = Property<string>(args, "outputDir");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "metadata.json"), "{}");
            return Task.FromResult((T)(object)new[]
            {
                new SyncResult { PlcName = "PLC_1", ExportRoot = output },
            });
        }
    }

    private sealed class BlockingEngineeringCaller : FakeToolCaller
    {
        public TaskCompletionSource ExportEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseExport { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (tool == "rebuild_export")
            {
                ExportEntered.SetResult();
                await ReleaseExport.Task.WaitAsync(cancellationToken);
                var output = Property<string>(args, "outputDir");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "metadata.json"), "{}");
                return (T)(object)new[]
                {
                    new SyncResult { PlcName = "PLC_1", ExportRoot = output },
                };
            }

            if (tool == "connect")
                return (T)(object)new object();

            throw new InvalidOperationException(tool);
        }
    }

    private sealed class BlockingDiscoveryEngineeringCaller : FakeToolCaller
    {
        public TaskCompletionSource ProjectInfoEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseProjectInfo { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (tool == "connect")
                return (T)(object)new object();
            if (tool == "get_project_info")
            {
                ProjectInfoEntered.SetResult();
                await ReleaseProjectInfo.Task.WaitAsync(cancellationToken);
                return (T)(object)new ProjectInfo
                {
                    Name = "Created",
                    Path = @"C:\Projects\Created.ap17",
                    PlcDevices = ["PLC_1"],
                };
            }
            throw new InvalidOperationException(tool);
        }
    }

    private sealed class BlockingImportEngineeringCaller : FakeToolCaller
    {
        public TaskCompletionSource ImportEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseImport { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (tool == "import_block")
            {
                ImportEntered.SetResult();
                await ReleaseImport.Task.WaitAsync(cancellationToken);
                return (T)(object)new ImportResult
                {
                    BlockName = "A",
                    Success = true,
                    ImportedAt = DateTime.UtcNow,
                };
            }
            if (tool == "compile_block")
                return (T)(object)new CompileResult { BlockName = "A", State = "success" };
            if (tool == "connect")
                return (T)(object)new object();
            throw new InvalidOperationException(tool);
        }
    }

    private sealed class FailSecondAndThirdMoveOperations : IStagingFileOperations
    {
        private int moves;

        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool FileExists(string path) => File.Exists(path);
        public void CreateDirectory(string path) => Directory.CreateDirectory(path);
        public void MoveDirectory(string source, string destination)
        {
            moves++;
            if (moves is 2 or 3)
            {
                throw new IOException($"Injected move failure {moves}.");
            }

            Directory.Move(source, destination);
        }
        public void DeleteDirectory(string path) => Directory.Delete(path);
        public IEnumerable<string> EnumerateEntries(string path) =>
            Directory.EnumerateFileSystemEntries(path);
        public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
        public void DeleteFile(string path) => File.Delete(path);
    }
    private sealed class Fixture
    {
        private readonly AtomicJsonStore store = new();

        private Fixture(DeviceContext context) => Context = context;
        public DeviceContext Context { get; }

        public static Fixture Create(
            string parent,
            bool knowledgeStale = false,
            string? sourceProjectPath = @"C:\Projects\Line.ap17")
        {
            var context = WorkbenchPaths.ResolveDevice(
                "wb-1", Path.Combine(parent, Guid.NewGuid().ToString("N")),
                "wt-1", "master", "dev-1", "PLC_1");
            Directory.CreateDirectory(context.ExportedSourceRoot);
            Directory.CreateDirectory(context.ModifiedSourceRoot);
            Directory.CreateDirectory(context.StagingRoot);
            new AtomicJsonStore().Write(
                Path.Combine(context.DeviceRoot, "device.json"),
                new DeviceMetadata(
                    WorkbenchSchema.CurrentVersion, "dev-1", "wt-1", "PLC_1", "PLC_1",
                    null, null, null,
                    new KnowledgeState(knowledgeStale, new Dictionary<string, string>(), null),
                    Array.Empty<DeviceImportRecord>()));
            new AtomicJsonStore().Write(
                Path.Combine(context.WorktreeRoot, "worktree.json"),
                new WorktreeMetadata(
                    WorkbenchSchema.CurrentVersion, "wt-1", "wb-1", "master", "master",
                    "2026-07-29T00:00:00Z", null, null, sourceProjectPath,
                    new[] { "dev-1" }, null));
            return new Fixture(context);
        }

        public void WriteBaseline(string relative, string content) =>
            Write(Context.ExportedSourceRoot, relative, content);
        public void WriteStaging(string relative, string content) =>
            Write(Context.StagingRoot, relative, content);
        public void WriteModified(string relative, string content) =>
            Write(Context.ModifiedSourceRoot, relative, content);

        public void WriteManifests(params string[] staging)
        {
            WriteManifest(Context.StagingRoot, staging);
            var baseline = Directory.EnumerateFiles(
                    Context.ExportedSourceRoot, "*.xml", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Context.ExportedSourceRoot, path).Replace('\\', '/'))
                .ToArray();
            if (baseline.Length > 0)
            {
                WriteManifest(Context.ExportedSourceRoot, baseline);
            }
        }

        private static void Write(string parent, string relative, string content)
        {
            var path = Path.Combine(parent, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private static void WriteManifest(string parent, IEnumerable<string> paths)
        {
            var components = paths.Select(path => new
            {
                name = Path.GetFileNameWithoutExtension(path),
                sourcePath = $"Program blocks/{Path.GetFileNameWithoutExtension(path)}",
                category = "FC",
                status = "Exported",
                exportedFile = path.Replace('\\', '/'),
            }).ToArray();
            File.WriteAllText(
                Path.Combine(parent, "metadata.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    exportStartedUtc = "2026-07-27T00:00:00Z",
                    exportFinishedUtc = "2026-07-27T00:00:01Z",
                    exportRoot = parent,
                    components,
                }));
        }
    }
}
