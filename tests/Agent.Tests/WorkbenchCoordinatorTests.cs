using Agent.Mcp;
using Agent.Workbench;
using Contracts.Engineering;
using Contracts.Knowledge;
using Contracts.Sandbox;
using System.Security.Cryptography;
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
    public async Task ReloadHardwareRejectsWhenAttachedTiaProjectDoesNotMatchSelectedWorktree()
    {
        var fixture = Fixture.Create(root, sourceProjectPath: @"C:\Projects\ProjectB.ap17");
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo
            {
                Name = "ProjectA",
                Path = @"C:\Projects\ProjectA.ap17",
            })
            .Respond("disconnect", new object())
            .Respond("list_sessions", Array.Empty<SessionInfo>())
            .Respond("connect", new { connected = true })
            .Respond("get_project_info", new ProjectInfo
            {
                Name = "ProjectA",
                Path = @"C:\Projects\ProjectA.ap17",
            });
        var coordinator = Create(fixture, engineering: engineering);

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
            coordinator.ReloadHardwareAsync(fixture.Context, CancellationToken.None));

        Assert.Equal("ENGINEERING_PROJECT_MISMATCH", error.Code);
        Assert.Contains("ProjectB.ap17", error.Message);
        Assert.Contains("ProjectA.ap17", error.Message);
        Assert.Equal(["get_project_info", "disconnect", "list_sessions", "connect", "get_project_info"], engineering.Calls);
        Assert.False(Directory.Exists(WorkbenchPaths.ResolveHardwareRoot(fixture.Context.WorktreeRoot)));
    }

    [Fact]
    public async Task ReloadHardwareOpensRegisteredProjectBeforeExportWhenAttachedProjectDiffers()
    {
        var fixture = Fixture.Create(root, sourceProjectPath: @"C:\Projects\ProjectB.ap17");
        var hardwareRoot = Path.Combine(
            WorkbenchPaths.ResolveHardwareRoot(fixture.Context.WorktreeRoot), "Hardware");
        Directory.CreateDirectory(hardwareRoot);
        File.WriteAllText(Path.Combine(hardwareRoot, "project.aml"), "<CAEXFile />");
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo
            {
                Name = "ProjectA",
                Path = @"C:\Projects\ProjectA.ap17",
            })
            .Respond("disconnect", new object())
            .Respond("list_sessions", Array.Empty<SessionInfo>())
            .Respond("connect", new { connected = true })
            .Respond("get_project_info", new ProjectInfo
            {
                Name = "ProjectB",
                Path = @"C:\Projects\ProjectB.ap17",
            })
            .Respond("export_hardware_configuration", new[]
            {
                new HardwareExportResult
                {
                    Scope = "project",
                    Success = true,
                    ContentHash = "project-hash",
                },
            });
        var versionControl = new FakeToolCaller()
            .Respond("vc_add", new object())
            .Respond("vc_commit", new CoordinatorGitCommitResult { Sha = "commit-1" });
        var coordinator = Create(fixture, engineering: engineering, versionControl: versionControl);

        var result = await coordinator.ReloadHardwareAsync(fixture.Context, CancellationToken.None);

        Assert.Equal("commit-1", result.CommitSha);
        Assert.Equal(
            ["get_project_info", "disconnect", "list_sessions", "connect", "get_project_info", "export_hardware_configuration"],
            engineering.Calls);
        Assert.Equal(@"C:\Projects\ProjectB.ap17", Property<string>(
            engineering.CallArgs["connect"].Single(), "projectPath"));
        Assert.True(Property<bool>(engineering.CallArgs["connect"].Single(), "withUI"));
    }

    [Fact]
    public async Task CompareHardwareRejectsWhenAttachedTiaProjectDoesNotMatchSelectedWorktree()
    {
        var fixture = Fixture.Create(root, sourceProjectPath: @"C:\Projects\ProjectB.ap17");
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo
            {
                Name = "ProjectA",
                Path = @"C:\Projects\ProjectA.ap17",
            })
            .Respond("disconnect", new object())
            .Respond("list_sessions", Array.Empty<SessionInfo>())
            .Respond("connect", new { connected = true })
            .Respond("get_project_info", new ProjectInfo
            {
                Name = "ProjectA",
                Path = @"C:\Projects\ProjectA.ap17",
            });
        var coordinator = Create(fixture, engineering: engineering);

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
            coordinator.CompareHardwareAsync(fixture.Context, CancellationToken.None));

        Assert.Equal("ENGINEERING_PROJECT_MISMATCH", error.Code);
        Assert.Equal(["get_project_info", "disconnect", "list_sessions", "connect", "get_project_info"], engineering.Calls);
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(fixture.Context.WorktreeRoot),
            path => Path.GetFileName(path).StartsWith(".hardware-compare-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompareHardwareKeepsLiveExportInHardwareStagingForApproval()
    {
        var fixture = Fixture.Create(root);
        var engineering = new HardwareExportCaller();
        var coordinator = Create(fixture, engineering: engineering);

        await coordinator.CompareHardwareAsync(fixture.Context, CancellationToken.None);

        var stagingRoot = Path.Combine(
            WorkbenchPaths.ResolveHardwareRoot(fixture.Context.WorktreeRoot),
            "staging");
        Assert.Equal(stagingRoot, Property<string>(
            engineering.CallArgs["export_hardware_configuration"].Single(), "outputDir"));
        Assert.True(File.Exists(Path.Combine(stagingRoot, "project.aml")));
        Assert.True(Directory.Exists(stagingRoot));
    }

    [Fact]
    public async Task HardwareOverwriteRequiresExplicitConfirmation()
    {
        var fixture = Fixture.Create(root);
        var coordinator = Create(fixture);

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
            coordinator.OverwriteHardwareFromStagingAsync(
                fixture.Context,
                confirmOverwrite: false,
                CancellationToken.None));

        Assert.Equal("HARDWARE_OVERWRITE_CONFIRMATION_REQUIRED", error.Code);
    }

    [Fact]
    public async Task HardwareOverwriteReplacesBaselineFromStagingAndCommitsIt()
    {
        var fixture = Fixture.Create(root);
        var hardwareRoot = WorkbenchPaths.ResolveHardwareRoot(fixture.Context.WorktreeRoot);
        var stagingRoot = WorkbenchPaths.ResolveHardwareStagingRoot(fixture.Context.WorktreeRoot);
        Directory.CreateDirectory(hardwareRoot);
        Directory.CreateDirectory(stagingRoot);
        File.WriteAllText(Path.Combine(hardwareRoot, "project.aml"), "<old />");
        File.WriteAllText(Path.Combine(stagingRoot, "project.aml"), "<new />");
        var versionControl = new FakeToolCaller()
            .Respond("vc_add", new object())
            .Respond("vc_commit", new CoordinatorGitCommitResult { Sha = "hardware-commit" });
        var coordinator = Create(fixture, versionControl: versionControl);

        var result = await coordinator.OverwriteHardwareFromStagingAsync(
            fixture.Context,
            confirmOverwrite: true,
            CancellationToken.None);

        Assert.Equal("hardware-commit", result.CommitSha);
        Assert.Equal("<new />", File.ReadAllText(Path.Combine(hardwareRoot, "project.aml")));
        Assert.Equal(["vc_add", "vc_commit"], versionControl.Calls);
        Assert.Contains(
            "hardware/project.aml",
            Property<string[]>(versionControl.CallArgs["vc_add"].Single(), "paths"));
    }

    [Fact]
    public async Task ReloadHardwareKeepsUsableProjectAmlWhenOptionalDeviceExportFails()
    {
        var fixture = Fixture.Create(root);
        var hardwareRoot = Path.Combine(
            WorkbenchPaths.ResolveHardwareRoot(fixture.Context.WorktreeRoot), "Hardware");
        Directory.CreateDirectory(hardwareRoot);
        File.WriteAllText(Path.Combine(hardwareRoot, "project.aml"), "<CAEXFile />");
        var engineering = new FakeToolCaller()
            .Respond("get_project_info", new ProjectInfo { Path = @"C:\Projects\Line.ap17" })
            .Respond("export_hardware_configuration", new[]
            {
                new HardwareExportResult
                {
                    Scope = "project",
                    Success = false,
                    AmlFilePath = Path.Combine(hardwareRoot, "project.aml"),
                    Error = "project export reported warnings",
                },
                new HardwareExportResult
                {
                    Scope = "device",
                    DeviceName = "HMI_1",
                    Success = false,
                    Error = "TypeIdentifier is invalid or missing",
                },
            });
        var versionControl = new FakeToolCaller()
            .Respond("vc_add", new object())
            .Respond("vc_commit", new CoordinatorGitCommitResult { Sha = "partial-commit" });
        var coordinator = Create(fixture, engineering: engineering, versionControl: versionControl);

        var result = await coordinator.ReloadHardwareAsync(fixture.Context, CancellationToken.None);

        Assert.Equal("partial-commit", result.CommitSha);
        Assert.Contains(result.Warnings!, warning => warning.Contains("HMI_1"));
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
        var versionControl = ScriptCreateVersionControl(new FakeToolCaller());
        var coordinator = Create(
            fixture,
            engineering: engineering,
            versionControl: versionControl);
        var createRoot = Path.Combine(root, "created");

        var creating = coordinator.CreateWorkbenchAsync(
            new CreateWorkbenchRequest("Created", createRoot, 42, null));
        await engineering.ProjectInfoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var opening = coordinator.OpenProjectInTiaAsync(
            fixture.Context,
            CancellationToken.None);

        await Task.Yield();
        Assert.False(opening.IsCompleted);
        Assert.Equal(["connect", "get_project_info"], engineering.Calls);

        engineering.ReleaseProjectInfo.SetResult();
        await Task.WhenAll(creating, opening);
        Assert.Equal(
            new[]
            {
                "connect", "get_project_info", "save_project_as", "get_project_info",
                "compile_plc", "get_plc_checksums",
            },
            engineering.Calls.Take(6).ToArray());
        // The engineering session is released between the managed-copy phase and the export,
        // so the waiting OpenProjectInTia connect interleaves with the bootstrap tail.
        Assert.Equal(9, engineering.Calls.Count);
        Assert.Equal(2, engineering.Calls.Count(call => call == "connect"));
        Assert.True(
            engineering.Calls.IndexOf("rebuild_export") < engineering.Calls.IndexOf("disconnect"));
    }

    [Fact]
    public async Task CancelledWorkbenchDiscoveryReleasesEngineeringSession()
    {
        var fixture = Fixture.Create(root, sourceProjectPath: @"C:\Projects\Existing.ap17");
        var engineering = new BlockingDiscoveryEngineeringCaller();
        var versionControl = ScriptCreateVersionControl(new FakeToolCaller());
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
                null),
            cancellation.Token);
        await engineering.ProjectInfoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creating);
        await coordinator.OpenProjectInTiaAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(["connect", "get_project_info", "disconnect", "connect"], engineering.Calls);
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
    public async Task CreateInitializesStoresThenImportsAndBootstrapsManagedProject()
    {
        var calls = new List<string>();
        var versionControl = ScriptCreateVersionControl(Caller(calls));
        var engineering = ScriptCreateEngineering(Caller(calls), new[] { "PLC_1", "PLC_2" });
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
                null),
            progress: progress);

        Assert.Equal(
            new[]
            {
                "version:vc_init_shared",
                "version:svn_init_shared",
                "version:svn_checkout",
                "engineering:connect",
                "engineering:get_project_info",
                "engineering:save_project_as",
                "engineering:get_project_info",
                "engineering:compile_plc",
                "engineering:compile_plc",
                "engineering:get_plc_checksums",
                "engineering:rebuild_export",
                "engineering:rebuild_export",
                "engineering:disconnect",
                "version:svn_commit",
                "version:vc_commit_selected",
            },
            calls);
        Assert.Equal(
            new[]
            {
                "Preparing workbench storage...",
                "Initializing Git repository...",
                "Initializing SVN native store...",
                "Attaching to TIA Portal...",
                "Saving the managed project copy into the workbench...",
                "Verifying the managed project copy...",
                "Compiling PLC_1 on the managed project...",
                "Compiling PLC_2 on the managed project...",
                "Creating device folders...",
            },
            progress.Messages.Take(9).ToArray());
        Assert.Contains("Closing the TIA session...", progress.Messages);
        Assert.Contains("Committing the native TIA baseline to SVN...", progress.Messages);
        Assert.Contains("Creating the initial PLC source baseline commit...", progress.Messages);
        Assert.Equal(
            42,
            Property<int>(engineering.CallArgs["connect"].Single(), "sessionId"));
        Assert.Equal(2, result.Devices.Count);
        Assert.All(result.Devices, device =>
        {
            var context = catalog.ResolveDevice(result.Workbench, result.Worktree, device);
            Assert.True(File.Exists(Path.Combine(context.DeviceRoot, "device.json")));
            Assert.True(File.Exists(Path.Combine(context.SourceRoot, "Blocks", "Main.xml")));
        });
        var masterRoot = Path.Combine(result.Workbench.RootPath, "worktrees", "master");
        Assert.True(File.Exists(Path.Combine(masterRoot, "worktree.json")));
        Assert.True(File.Exists(Path.Combine(
            masterRoot, "engineering-state", "revision.json")));
        var managedPath = Assert.IsType<string>(result.Workbench.ManagedTiaProjectPath);
        Assert.Equal(managedPath, result.Workbench.SourceProjectPath);
        Assert.Equal(managedPath, result.Worktree.ManagedTiaProjectPath);
        Assert.Equal(@"C:\Projects\Line.ap17", result.Workbench.OriginProjectPath);
        Assert.NotNull(result.Workbench.OriginImportedAt);
        Assert.Equal(
            Path.Combine(result.Workbench.RootPath, "repository.svn"),
            result.Workbench.SvnRepositoryPath);

        var revision = EngineeringStateWriter.Read(Path.Combine(
            masterRoot, "engineering-state", "revision.json"));
        Assert.Equal("^/native/main", revision.Svn.Url);
        Assert.Equal(1, revision.Svn.Revision);
        Assert.Equal("PLC_1:checksum-PLC_1;PLC_2:checksum-PLC_2", revision.Tia.ProjectChecksum);
        Assert.Null(revision.Safety.FSignature);
        Assert.Equal(EngineeringCompileStatus.Success, revision.Validation.CompileStatus);

        var commitArgs = versionControl.CallArgs["vc_commit_selected"].Single();
        var committedPaths = Property<string[]>(commitArgs, "paths");
        Assert.Contains(EngineeringStateWriter.RelativePath, committedPaths);
        Assert.Contains("devices/PLC_1/source/Blocks/Main.xml", committedPaths);
        Assert.Contains("devices/PLC_2/source/Blocks/Main.xml", committedPaths);
    }

    [Fact]
    public async Task CreateCompileFailureStillCompletesImportWithFailedStatus()
    {
        var versionControl = ScriptCreateVersionControl(new FakeToolCaller());
        var engineering = ScriptCreateEngineering(
            new FakeToolCaller(), new[] { "PLC_1" }, compileState: "error");
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

        var result = await coordinator.CreateWorkbenchAsync(
            new CreateWorkbenchRequest("Line", Path.Combine(root, "compile-failure"), 42, null));

        var revision = EngineeringStateWriter.Read(Path.Combine(
            result.Workbench.RootPath, "worktrees", "master", "engineering-state", "revision.json"));
        Assert.Equal(EngineeringCompileStatus.Failed, revision.Validation.CompileStatus);
        Assert.Null(revision.Tia.ProjectChecksum);
        Assert.DoesNotContain("get_plc_checksums", engineering.Calls);
    }

    [Fact]
    public async Task CreateWorkbenchCreatesOnlySourceStagingAndRuntimeDeviceArtifacts()
    {
        var versionControl = ScriptCreateVersionControl(new FakeToolCaller());
        var engineering = ScriptCreateEngineering(new FakeToolCaller(), new[] { "PLC_1" });
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

        var result = await coordinator.CreateWorkbenchAsync(
            new CreateWorkbenchRequest(
                "Line",
                Path.Combine(root, "single-source"),
                42,
                null));

        var device = Assert.Single(result.Devices);
        var context = catalog.ResolveDevice(result.Workbench, result.Worktree, device);
        Assert.True(Directory.Exists(context.SourceRoot));
        Assert.True(Directory.Exists(context.StagingRoot));
        Assert.True(File.Exists(Path.Combine(context.DeviceRoot, "device.json")));
        Assert.True(File.Exists(Path.Combine(context.WorktreeRoot, "worktree.json")));
        Assert.False(Directory.Exists(Path.Combine(context.DeviceRoot, "exported-source")));
        Assert.False(Directory.Exists(Path.Combine(context.DeviceRoot, "modified-source")));
        Assert.Equal(
            Path.Combine(context.DeviceRoot, "plc-knowledge.db"),
            context.KnowledgeDbPath);
    }

    [Fact]
    public async Task CreateFeatureCopiesIgnoredDeviceMetadataLoadedBeforeLinkedCheckout()
    {
        var workbenchRoot = Path.Combine(root, "feature-inheritance");
        var masterDevicePath = Path.Combine(
            workbenchRoot,
            "worktrees",
            "master",
            "devices",
            "PLC_1",
            "device.json");
        var versionControl = new MetadataRemovingWorktreeCaller(masterDevicePath);
        var engineering = ScriptCreateEngineering(new FakeToolCaller(), new[] { "PLC_1" });
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var coordinator = new WorkbenchCoordinator(
            engineering,
            new FakeToolCaller(),
            versionControl,
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        var created = await coordinator.CreateWorkbenchAsync(
            new CreateWorkbenchRequest("Line", workbenchRoot, 42, null));
        var masterDevice = Assert.Single(created.Devices) with
        {
            LastExportChecksum = "master-checksum",
            LastExportUtc = "2026-08-04T08:00:00Z",
            Knowledge = new KnowledgeState(
                false,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Blocks/Main.xml"] = "source-hash",
                },
                "2026-08-04T08:01:00Z"),
        };
        store.Write(masterDevicePath, masterDevice);

        var feature = await coordinator.CreateWorktreeAsync(
            new CreateWorktreeRequest(
                created.Workbench,
                "feature-a",
                "feature-a",
                "master"));

        Assert.Equal(created.Worktree.DeviceIds, feature.DeviceIds);
        var featureDevicePath = Path.Combine(
            workbenchRoot,
            "worktrees",
            "feature-a",
            "devices",
            "PLC_1",
            "device.json");
        var inherited = store.Read<DeviceMetadata>(featureDevicePath);
        Assert.Equal(masterDevice.DeviceId, inherited.DeviceId);
        Assert.Equal(feature.WorktreeId, inherited.WorktreeId);
        Assert.Equal(masterDevice.PlcName, inherited.PlcName);
        Assert.Equal(masterDevice.EngineeringIdentity, inherited.EngineeringIdentity);
        Assert.Equal(masterDevice.LastExportChecksum, inherited.LastExportChecksum);
        Assert.Equal(masterDevice.LastExportUtc, inherited.LastExportUtc);
        Assert.Equal(masterDevice.Knowledge.Stale, inherited.Knowledge.Stale);
        Assert.Equal(masterDevice.Knowledge.UpdatedAt, inherited.Knowledge.UpdatedAt);
        Assert.Equal(masterDevice.Knowledge.BaselineStale, inherited.Knowledge.BaselineStale);
        Assert.Equal(
            masterDevice.Knowledge.AppliedOverlayHashes.OrderBy(pair => pair.Key),
            inherited.Knowledge.AppliedOverlayHashes.OrderBy(pair => pair.Key));
        Assert.True(Directory.Exists(Path.Combine(
            workbenchRoot, "worktrees", "feature-a", "devices", "PLC_1", "source")));
        Assert.True(Directory.Exists(Path.Combine(
            workbenchRoot, "worktrees", "feature-a", "devices", "PLC_1", "staging")));
        Assert.Equal(
            new[]
            {
                "vc_init_shared", "svn_init_shared", "svn_checkout", "svn_commit",
                "vc_commit_selected", "vc_add_worktree",
            },
            versionControl.Calls);
    }

    [Fact]
    public async Task CreateFeatureMetadataFailureRollsBackCheckoutAndNewBranch()
    {
        var workbenchRoot = Path.Combine(root, "feature-metadata-failure");
        var store = new AtomicJsonStore();
        var versionControl = ScriptCreateVersionControl(new FakeToolCaller())
            .Respond("vc_add_worktree", new object())
            .Respond("vc_remove_worktree", new object());
        var engineering = ScriptCreateEngineering(new FakeToolCaller(), new[] { "PLC_1" });
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var coordinator = new WorkbenchCoordinator(
            engineering,
            new FakeToolCaller(),
            versionControl,
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        var created = await coordinator.CreateWorkbenchAsync(
            new CreateWorkbenchRequest("Line", workbenchRoot, 42, null));
        var masterDevicePath = Path.Combine(
            workbenchRoot, "worktrees", "master", "devices", "PLC_1", "device.json");
        store.Write(
            masterDevicePath,
            Assert.Single(created.Devices) with { PlcName = string.Empty });

        await Assert.ThrowsAsync<WorkbenchPathException>(() =>
            coordinator.CreateWorktreeAsync(
                new CreateWorktreeRequest(created.Workbench, "feature-a", "feature-a"),
                CancellationToken.None));

        Assert.Equal(
            new[]
            {
                "vc_init_shared", "svn_init_shared", "svn_checkout", "svn_commit",
                "vc_commit_selected", "vc_add_worktree", "vc_remove_worktree",
            },
            versionControl.Calls);
        var rollback = versionControl.CallArgs["vc_remove_worktree"].Single();
        Assert.Equal("feature-a", Property<string>(rollback, "branchName"));
        Assert.True(Property<bool>(rollback, "deleteBranch"));
        Assert.DoesNotContain(
            catalog.Load(workbenchRoot).Worktrees,
            item => item.Branch == "feature-a");
    }

    [Fact]
    public async Task CreateFeaturePartialGitCreationRequestsSafeBranchRollback()
    {
        var workbenchRoot = Path.Combine(root, "feature-partial-creation");
        var store = new AtomicJsonStore();
        var versionControl = new PartialWorktreeCreationCaller();
        var engineering = ScriptCreateEngineering(new FakeToolCaller(), new[] { "PLC_1" });
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var coordinator = new WorkbenchCoordinator(
            engineering,
            new FakeToolCaller(),
            versionControl,
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        var created = await coordinator.CreateWorkbenchAsync(
            new CreateWorkbenchRequest("Line", workbenchRoot, 42, null));

        await Assert.ThrowsAsync<ToolCallException>(() =>
            coordinator.CreateWorktreeAsync(
                new CreateWorktreeRequest(created.Workbench, "feature-a", "feature-a"),
                CancellationToken.None));

        Assert.Equal(
            new[]
            {
                "vc_init_shared", "svn_init_shared", "svn_checkout", "svn_commit",
                "vc_commit_selected", "vc_add_worktree", "vc_remove_worktree",
            },
            versionControl.Calls);
        var rollback = versionControl.CallArgs["vc_remove_worktree"].Single();
        Assert.Equal("feature-a", Property<string>(rollback, "branchName"));
        Assert.True(Property<bool>(rollback, "deleteBranch"));
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
                    null)));

        Assert.False(File.Exists(Path.Combine(workbenchRoot, "workbench.json")));
        Assert.False(Directory.Exists(Path.Combine(workbenchRoot, "repository.git")));
        Assert.False(Directory.Exists(Path.Combine(workbenchRoot, "worktrees")));
    }

    [Fact]
    public async Task RestoreTiaProjectRejectsWorkbenchWithoutSvnNativeStore()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var workbenchRoot = Path.Combine(root, "legacy-wb");
        Directory.CreateDirectory(workbenchRoot);
        store.Write(
            Path.Combine(workbenchRoot, "workbench.json"),
            new WorkbenchMetadata(
                "1.1",
                "wb-legacy",
                "Legacy",
                "2026-08-01T00:00:00.0000000Z",
                workbenchRoot,
                Path.Combine(workbenchRoot, "repository.git"),
                null,
                @"C:\Projects\Line.ap17",
                Array.Empty<WorkbenchWorktreeRegistration>()));
        var coordinator = new WorkbenchCoordinator(
            new FakeToolCaller(),
            new FakeToolCaller(),
            new FakeToolCaller(),
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        coordinator.RegisterWorkbench(catalog.Load(workbenchRoot));

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
            coordinator.RestoreTiaProjectAsync(
                "wb-legacy", "wt-1", Path.Combine(root, "restore-target")));

        Assert.Equal("SVN_HISTORY_UNAVAILABLE", error.Code);
    }

    [Fact]
    public async Task AttachTiaInstanceConnectsBySessionIdOnly()
    {
        var fixture = Fixture.Create(root, sourceProjectPath: @"C:\Projects\Line.ap17");
        var engineering = new FakeToolCaller().Respond("connect", new { connected = true });
        var coordinator = Create(fixture, engineering: engineering);

        await coordinator.AttachTiaInstanceAsync(4242, CancellationToken.None);

        var args = engineering.CallArgs["connect"].Single();
        Assert.Equal(4242, Property<int>(args, "sessionId"));
        Assert.Null(args.GetType().GetProperty("projectPath"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(42, @"C:\Projects\Line.ap17")]
    public async Task CreateRejectsAmbiguousEngineeringConnectionBeforeCreatingAnything(
        int? sessionId,
        string? projectPath)
    {
        var engineering = new FakeToolCaller();
        var versionControl = new FakeToolCaller();
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
        var workbenchRoot = Path.Combine(root, $"Ambiguous-{Guid.NewGuid():N}");

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
            coordinator.CreateWorkbenchAsync(
                new CreateWorkbenchRequest("Ambiguous", workbenchRoot, sessionId, projectPath)));

        Assert.Equal("ENGINEERING_CONNECTION_INVALID", error.Code);
        Assert.Empty(engineering.Calls);
        Assert.Empty(versionControl.Calls);
        Assert.False(Directory.Exists(workbenchRoot));
    }

    [Fact]
    public async Task CreateWithProjectPathImportsOriginHeadless()
    {
        var calls = new List<string>();
        var originPath = Path.Combine(root, "origin", "Line.ap17");
        Directory.CreateDirectory(Path.GetDirectoryName(originPath)!);
        File.WriteAllText(originPath, "origin project placeholder");
        var versionControl = ScriptCreateVersionControl(Caller(calls));
        var engineering = ScriptCreateEngineering(Caller(calls), new[] { "PLC_1" }, originPath);
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

        var result = await coordinator.CreateWorkbenchAsync(
            new CreateWorkbenchRequest(
                "Line",
                Path.Combine(root, "LineFromPath"),
                null,
                originPath));

        var args = engineering.CallArgs["connect"].Single();
        Assert.Equal(originPath, Property<string>(args, "projectPath"));
        Assert.False(Property<bool>(args, "withUI"));
        Assert.Null(args.GetType().GetProperty("sessionId"));
        Assert.Equal(
            new[]
            {
                "version:vc_init_shared",
                "version:svn_init_shared",
                "version:svn_checkout",
                "engineering:connect",
                "engineering:get_project_info",
                "engineering:save_project_as",
                "engineering:get_project_info",
                "engineering:compile_plc",
                "engineering:get_plc_checksums",
                "engineering:rebuild_export",
                "engineering:disconnect",
                "version:svn_commit",
                "version:vc_commit_selected",
            },
            calls);
        var managedPath = Assert.IsType<string>(result.Workbench.ManagedTiaProjectPath);
        Assert.Equal(managedPath, result.Workbench.SourceProjectPath);
        Assert.Equal(originPath, result.Workbench.OriginProjectPath);
        Assert.Single(result.Devices);
    }

    [Fact]
    public async Task CreateWithMissingOriginProjectFailsBeforeCreatingAnything()
    {
        var engineering = new FakeToolCaller();
        var versionControl = new FakeToolCaller();
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
        var workbenchRoot = Path.Combine(root, "MissingOrigin");

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
            coordinator.CreateWorkbenchAsync(
                new CreateWorkbenchRequest(
                    "MissingOrigin",
                    workbenchRoot,
                    null,
                    Path.Combine(root, "missing", "Line.ap17"))));

        Assert.Equal("ENGINEERING_ORIGIN_NOT_FOUND", error.Code);
        Assert.Empty(engineering.Calls);
        Assert.Empty(versionControl.Calls);
        Assert.False(Directory.Exists(workbenchRoot));
    }

    [Fact]
    public async Task CreateWithProjectPathOutsideSandboxFailsBeforeCreatingAnything()
    {
        var allowedRoot = Path.Combine(root, "allowed");
        Directory.CreateDirectory(allowedRoot);
        var engineering = new FakeToolCaller();
        var versionControl = new FakeToolCaller();
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
            new DeviceSourceResolver(_ => { }),
            pathJail: new PathJail(new[] { allowedRoot }));
        var workbenchRoot = Path.Combine(root, "Denied");

        var error = await Assert.ThrowsAsync<SandboxException>(() =>
            coordinator.CreateWorkbenchAsync(
                new CreateWorkbenchRequest(
                    "Denied",
                    workbenchRoot,
                    null,
                    @"C:\Projects\Line.ap17")));

        Assert.Equal("SANDBOX_PATH_DENIED", error.Code);
        Assert.Contains(allowedRoot, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(engineering.Calls);
        Assert.Empty(versionControl.Calls);
        Assert.False(Directory.Exists(workbenchRoot));
    }

    [Fact]
    public async Task CreateWithSessionProjectOutsideSandboxRollsBackWorkbench()
    {
        var allowedRoot = Path.Combine(root, "allowed");
        Directory.CreateDirectory(allowedRoot);
        var engineering = new FakeToolCaller()
            .Respond("connect", new object())
            .Respond("get_project_info", new ProjectInfo
            {
                Name = "Line",
                Path = @"C:\Projects\Line.ap17",
                PlcDevices = new[] { "PLC_1" },
            });
        var versionControl = new FakeToolCaller()
            .Respond("vc_init_shared", new object())
            .Respond("svn_init_shared", new CoordinatorSvnInitResult
            {
                RepositoryPath = "repository.svn",
                RepositoryUri = "file:///repository.svn/",
            })
            .Respond("svn_checkout", new object());
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
            new DeviceSourceResolver(_ => { }),
            pathJail: new PathJail(new[] { allowedRoot }));
        var workbenchRoot = Path.Combine(root, "SessionDenied");

        var error = await Assert.ThrowsAsync<SandboxException>(() =>
            coordinator.CreateWorkbenchAsync(
                new CreateWorkbenchRequest("SessionDenied", workbenchRoot, 42, null)));

        Assert.Equal("SANDBOX_PATH_DENIED", error.Code);
        Assert.False(File.Exists(Path.Combine(workbenchRoot, "workbench.json")));
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
                Failed = new[] { new SyncChange { Name = "A", Category = "FC", Reason = "export failed" } },
            },
        });
        var coordinator = Create(fixture, engineering: engineering);

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(
            () => coordinator.StageRefreshAsync(fixture.Context, CancellationToken.None));

        Assert.Equal("DEVICE_EXPORT_INCOMPLETE", error.Code);
        Assert.Contains("FC A", error.Message);
        Assert.Contains("export failed", error.Message);
        Assert.Equal("previous", File.ReadAllText(
            Path.Combine(fixture.Context.StagingRoot, "sentinel.txt")));
    }

    [Fact]
    public async Task StageRefreshRequestsCompileApprovalForInconsistentBlockWithoutCompiling()
    {
        var fixture = Fixture.Create(root);
        var engineering = new MutatingExportCaller(new[]
        {
            new SyncResult
            {
                PlcName = "PLC_1",
                ChecksumAfter = null,
                Failed = new[]
                {
                    new SyncChange
                    {
                        Name = "A",
                        Category = "FC",
                        Reason = "Block 'A' is inconsistent. Compile it first before export.",
                    },
                },
            },
        });
        var coordinator = Create(fixture, engineering: engineering);

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(
            () => coordinator.StageRefreshAsync(fixture.Context, CancellationToken.None));

        Assert.Equal("PLC_COMPILE_REQUIRED", error.Code);
        Assert.Contains("FC A", error.Message);
        Assert.DoesNotContain("compile_plc", engineering.Calls);
    }

    [Fact]
    public async Task StageRefreshCompilesSelectedPlcAndRetriesExportWhenApproved()
    {
        var fixture = Fixture.Create(root);
        var engineering = new CompileRetryExportCaller();
        var coordinator = Create(fixture, engineering: engineering);

        await coordinator.StageRefreshAsync(
            fixture.Context,
            CancellationToken.None,
            allowCompile: true);

        Assert.Equal(new[] { "rebuild_export", "compile_plc", "rebuild_export" }, engineering.Calls);
        Assert.Equal("PLC_1", Property<string>(engineering.CallArgs["compile_plc"].Single(), "plcName"));
        Assert.True(File.Exists(Path.Combine(fixture.Context.StagingRoot, "metadata.json")));
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
    public async Task ApplyRefreshWritesSelectedFilesAndLeavesThemPendingManualCommit()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteBaseline("Blocks/A.xml", "<old />");
        fixture.WriteStaging("Blocks/A.xml", "<new />");
        fixture.WriteStaging("Blocks/B.xml", "<added />");
        fixture.WriteManifests("Blocks/A.xml", "Blocks/B.xml");
        var calls = new List<string>();
        var versionControl = Caller(calls);
        var coordinator = Create(fixture, versionControl: versionControl);
        var preview = coordinator.PreviewRefresh(fixture.Context);

        var result = await coordinator.ApplyRefreshAsync(
            fixture.Context,
            new ApprovedReconciliation(
                preview,
                preview.Entries
                    .Where(entry => entry.Kind != ReconciliationChangeKind.Unchanged)
                    .Select(entry => entry.RelativePath)
                    .ToHashSet(StringComparer.Ordinal)),
            CancellationToken.None);

        Assert.Equal("FilesUpdated", result.State.ToString());
        Assert.Empty(calls);
        var expected = new[]
        {
            Relative(fixture.Context, "source/Blocks/A.xml"),
            Relative(fixture.Context, "source/Blocks/B.xml"),
            Relative(fixture.Context, "source/metadata.json"),
        };
        Assert.Equal(expected.Order(), result.ChangedPaths.Order());
        Assert.Null(result.CommitSha);
        Assert.Null(result.Error);
        Assert.Null(ReadDevice(fixture).LastReconciliationCommit);
    }

    [Fact]
    public async Task ApprovedMasterRefreshAutoCommitsSelectedSourceWithTheProvidedTitle()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteBaseline("Blocks/A.xml", "<old />");
        fixture.WriteStaging("Blocks/A.xml", "<new />");
        fixture.WriteManifests("Blocks/A.xml");
        var calls = new List<string>();
        var versionControl = Caller(calls)
            .Respond("vc_commit_selected", new WorkbenchCommitResult(
                "tia-commit-1",
                "Accept A from TIA",
                new[] { "devices/PLC_1/source/Blocks/A.xml" }));
        var coordinator = Create(fixture, versionControl: versionControl);
        var preview = coordinator.PreviewRefresh(fixture.Context);

        var result = await coordinator.ApplyRefreshAsync(
            fixture.Context,
            new ApprovedReconciliation(
                preview,
                preview.Entries
                    .Where(entry => entry.Kind != ReconciliationChangeKind.Unchanged)
                    .Select(entry => entry.RelativePath)
                    .ToHashSet(StringComparer.Ordinal)),
            CancellationToken.None,
            commitMessage: "Accept A from TIA");

        Assert.Equal("tia-commit-1", result.CommitSha);
        Assert.Equal(["vc_commit_selected"], versionControl.Calls);
        Assert.Equal(
            "Accept A from TIA",
            Property<string>(versionControl.CallArgs["vc_commit_selected"].Single(), "message"));
        Assert.Equal(
            new[] { "devices/PLC_1/source/Blocks/A.xml" },
            Property<IReadOnlyList<string>>(versionControl.CallArgs["vc_commit_selected"].Single(), "paths"));
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
        Assert.False(File.Exists(Path.Combine(fixture.Context.SourceRoot, "Blocks", "A.xml")));
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
            Path.Combine(fixture.Context.SourceRoot, "metadata.json"),
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

        Assert.Equal("NoChanges", result.State.ToString());
        Assert.Empty(result.ChangedPaths);
        Assert.Null(result.CommitSha);
        Assert.Null(result.Error);
        Assert.Empty(version.Calls);
        Assert.Equal(
            before,
            File.ReadAllBytes(Path.Combine(fixture.Context.DeviceRoot, "device.json")));
    }

    [Fact]
    public async Task ApplyRefreshDoesNotRequireAvailableVersionControl()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteBaseline("Blocks/A.xml", "<old />");
        fixture.WriteStaging("Blocks/A.xml", "<new />");
        fixture.WriteManifests("Blocks/A.xml");
        var versionControl = new FakeToolCaller()
            .Fail("vc_add", "GIT_ERROR", "version control must not be called");
        var coordinator = Create(fixture, versionControl: versionControl);
        var preview = coordinator.PreviewRefresh(fixture.Context);

        var result = await coordinator.ApplyRefreshAsync(
            fixture.Context,
            new ApprovedReconciliation(
                preview,
                preview.Entries
                    .Where(entry => entry.Kind != ReconciliationChangeKind.Unchanged)
                    .Select(entry => entry.RelativePath)
                    .ToHashSet(StringComparer.Ordinal)),
            CancellationToken.None);

        Assert.Equal("FilesUpdated", result.State.ToString());
        Assert.Equal("<new />", File.ReadAllText(
            Path.Combine(fixture.Context.SourceRoot, "Blocks", "A.xml")));
        Assert.Empty(versionControl.Calls);
        Assert.Null(result.CommitSha);
        Assert.Null(result.Error);
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
        Assert.Equal(fixture.Context.SourceRoot, Property<string>(args, "sourceRoot"));
        Assert.Null(args.GetType().GetProperty("exportedSourceRoot"));
        Assert.Null(args.GetType().GetProperty("modifiedSourceRoot"));
        Assert.Equal(fixture.Context.KnowledgeDbPath, Property<string>(args, "dbPath"));
        Assert.Equal(new[] { "Blocks/A.xml" }, Property<string[]>(args, "relativePaths"));
        var expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(
            Path.Combine(fixture.Context.SourceRoot, "Blocks", "A.xml")))).ToLowerInvariant();
        Assert.Equal(
            expectedHash,
            ReadDevice(fixture).Knowledge.AppliedOverlayHashes["Blocks/A.xml"]);
        Assert.False(ReadDevice(fixture).Knowledge.Stale);
        Assert.Equal(fixture.Context.KnowledgeDbPath, result.DbPath);
    }

    [Fact]
    public async Task SuccessfulIncrementalUpdateSkipsSecondUnchangedUpdate()
    {
        var fixture = Fixture.Create(root, knowledgeStale: true);
        File.WriteAllText(fixture.Context.KnowledgeDbPath, "exists");
        fixture.WriteModified("Blocks/A.xml", "<modified />");
        var update = new KnowledgeUpdateResult(
            fixture.Context.KnowledgeDbPath,
            new[] { "block:A" },
            new Dictionary<string, string> { ["block:A"] = "component-hash" },
            Array.Empty<string>());
        var knowledge = new FakeToolCaller()
            .Respond("update_components", update)
            .Respond("update_components", update);
        var coordinator = Create(fixture, knowledge: knowledge);

        await coordinator.UpdateKnowledgeAsync(fixture.Context, CancellationToken.None);
        await coordinator.UpdateKnowledgeAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(new[] { "update_components" }, knowledge.Calls);
        var hashes = ReadDevice(fixture).Knowledge.AppliedOverlayHashes;
        Assert.Equal(new[] { "Blocks/A.xml" }, hashes.Keys);
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
        var args = knowledge.CallArgs["ingest_source"].Single();
        Assert.Equal(fixture.Context.SourceRoot, Property<string>(args, "sourceRoot"));
        Assert.Null(args.GetType().GetProperty("exportedSourceRoot"));
        Assert.Null(args.GetType().GetProperty("modifiedSourceRoot"));
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
        var args = knowledge.CallArgs["ingest_source"].Single();
        Assert.Equal(fixture.Context.SourceRoot, Property<string>(args, "sourceRoot"));
        Assert.Null(args.GetType().GetProperty("exportedSourceRoot"));
        Assert.Null(args.GetType().GetProperty("modifiedSourceRoot"));
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
        var version = new FakeToolCaller();
        var knowledge = new FakeToolCaller()
            .Respond("ingest_source", new IngestResult { DbPath = fixture.Context.KnowledgeDbPath });
        var coordinator = Create(fixture, knowledge: knowledge, versionControl: version);
        var preview = coordinator.PreviewRefresh(fixture.Context);
        await coordinator.ApplyRefreshAsync(
            fixture.Context,
            new ApprovedReconciliation(
                preview,
                preview.Entries
                    .Where(entry => entry.Kind != ReconciliationChangeKind.Unchanged)
                    .Select(entry => entry.RelativePath)
                    .ToHashSet(StringComparer.Ordinal)),
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
        var overlay = Path.Combine(fixture.Context.SourceRoot, "Blocks", "A.xml");
        Assert.Equal(overlay, Property<string>(
            engineering.CallArgs["import_block"].Single(), "xmlFilePath"));
        Assert.True(File.Exists(overlay));
        Assert.Equal("success", result.CompileState);
        Assert.Single(ReadDevice(fixture).Imports);
    }

    [Fact]
    public async Task BootstrapStagesExportAndCreatesInitialBaselineCommit()
    {
        var fixture = Fixture.Create(root, knowledgeStale: true);
        var calls = new List<string>();
        var engineering = new BootstrapExportCaller(calls);
        var versionControl = Caller(calls)
            .Respond("vc_log", new ConsistencyLogResult())
            .Respond("vc_commit_selected", new WorkbenchCommitResult(
                "baseline-sha",
                "Initial PLC source baseline",
                new[] { "devices/PLC_1/source/Blocks/A.xml" }));
        var knowledge = Caller(calls)
            .Respond("ingest_source", new IngestResult { DbPath = fixture.Context.KnowledgeDbPath });
        var coordinator = Create(
            fixture,
            engineering: engineering,
            knowledge: knowledge,
            versionControl: versionControl);

        var result = await coordinator.BootstrapDeviceAsync(fixture.Context, CancellationToken.None);

        Assert.Equal("FilesUpdated", result.Baseline.State.ToString());
        Assert.Equal("baseline-sha", result.Baseline.CommitSha);
        Assert.Null(result.Baseline.Error);
        Assert.Equal(
            new[]
            {
                "engineering:rebuild_export",
                "version:vc_log",
                "version:vc_commit_selected",
                "knowledge:ingest_source",
            },
            calls);
        Assert.Equal("<a />", File.ReadAllText(
            Path.Combine(fixture.Context.SourceRoot, "Blocks", "A.xml")));
        Assert.Equal(new[] { "vc_log", "vc_commit_selected" }, versionControl.Calls);
        var device = ReadDevice(fixture);
        Assert.False(device.Knowledge.Stale);
        Assert.False(device.Knowledge.BaselineStale);
        Assert.Null(device.LastReconciliationCommit);
    }

    [Fact]
    public async Task BootstrapWorktreeStagesAndCommitsEveryRegisteredDevice()
    {
        var fixture = Fixture.Create(root, knowledgeStale: true);
        var second = fixture.AddDevice("dev-2", "PLC_2");
        var calls = new List<string>();
        var engineering = new MultiDeviceBootstrapExportCaller(calls);
        var versionControl = Caller(calls)
            .Respond("vc_log", new ConsistencyLogResult())
            .Respond("vc_commit_selected", new WorkbenchCommitResult(
                "baseline-sha",
                "Initial PLC source baseline",
                new[]
                {
                    "devices/PLC_1/source/Blocks/A.xml",
                    "devices/PLC_2/source/Blocks/A.xml",
                }));
        var knowledge = Caller(calls)
            .Respond("ingest_source", new IngestResult { DbPath = fixture.Context.KnowledgeDbPath })
            .Respond("ingest_source", new IngestResult { DbPath = second.KnowledgeDbPath });
        var coordinator = Create(
            fixture,
            engineering: engineering,
            knowledge: knowledge,
            versionControl: versionControl);

        var result = await coordinator.BootstrapWorktreeAsync(
            fixture.Context,
            CancellationToken.None);

        Assert.Equal(2, result.Devices.Count);
        Assert.All(result.Devices, device =>
        {
            Assert.Equal("FilesUpdated", device.Baseline.State.ToString());
            Assert.Equal("baseline-sha", device.Baseline.CommitSha);
        });
        Assert.Equal(
            new[] { "PLC_1", "PLC_2" },
            engineering.CallArgs["rebuild_export"]
                .Select(args => Property<string>(args, "plcName"))
                .ToArray());
        Assert.Equal(2, knowledge.Calls.Count(call => call == "ingest_source"));
        Assert.Equal(["vc_log", "vc_commit_selected"], versionControl.Calls);
        Assert.Equal(
            new[]
            {
                "devices/PLC_1/source/Blocks/A.xml",
                "devices/PLC_2/source/Blocks/A.xml",
            },
            Property<IReadOnlyList<string>>(
                versionControl.CallArgs["vc_commit_selected"].Single(),
                "paths"));
        Assert.Equal("<PLC_1 />", File.ReadAllText(Path.Combine(fixture.Context.SourceRoot, "Blocks", "A.xml")));
        Assert.Equal("<PLC_2 />", File.ReadAllText(Path.Combine(second.SourceRoot, "Blocks", "A.xml")));
    }

    [Fact]
    public async Task BootstrapCompilesAndRetriesExportWhenUserApprovedCompile()
    {
        var fixture = Fixture.Create(root, knowledgeStale: true);
        var engineering = new CompileRetryBootstrapCaller();
        var calls = new List<string>();
        var versionControl = Caller(calls)
            .Respond("vc_log", new ConsistencyLogResult())
            .Respond("vc_commit_selected", new WorkbenchCommitResult(
                "baseline-sha",
                "Initial PLC source baseline",
                new[] { "devices/PLC_1/source/Blocks/A.xml" }));
        var knowledge = Caller(calls)
            .Respond("ingest_source", new IngestResult { DbPath = fixture.Context.KnowledgeDbPath });
        var coordinator = Create(
            fixture,
            engineering: engineering,
            knowledge: knowledge,
            versionControl: versionControl);

        var result = await coordinator.BootstrapDeviceAsync(
            fixture.Context,
            CancellationToken.None,
            allowCompile: true);

        Assert.Equal("FilesUpdated", result.Baseline.State.ToString());
        Assert.Equal(
            new[] { "rebuild_export", "compile_plc", "rebuild_export" },
            engineering.Calls);
        Assert.Equal("PLC_1", Property<string>(engineering.CallArgs["compile_plc"].Single(), "plcName"));
        Assert.Equal("baseline-sha", result.Baseline.CommitSha);
        Assert.Equal(new[] { "vc_log", "vc_commit_selected" }, versionControl.Calls);
    }

    [Fact]
    public async Task BootstrapSurfacesCompileRequiredWithoutCompilingCommittingOrIngesting()
    {
        var fixture = Fixture.Create(root);
        var engineering = new MutatingExportCaller(new[]
        {
            new SyncResult
            {
                PlcName = "PLC_1",
                ChecksumAfter = null,
                Failed = new[]
                {
                    new SyncChange
                    {
                        Name = "A",
                        Category = "FC",
                        Reason = "Block 'A' is inconsistent. Compile it first before export.",
                    },
                },
            },
        });
        var versionControl = new FakeToolCaller();
        var knowledge = new FakeToolCaller();
        var coordinator = Create(
            fixture,
            engineering: engineering,
            knowledge: knowledge,
            versionControl: versionControl);

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(
            () => coordinator.BootstrapDeviceAsync(fixture.Context, CancellationToken.None));

        Assert.Equal("PLC_COMPILE_REQUIRED", error.Code);
        Assert.Empty(versionControl.Calls);
        Assert.Empty(knowledge.Calls);
        Assert.DoesNotContain("compile_plc", engineering.Calls);
        Assert.False(File.Exists(Path.Combine(fixture.Context.SourceRoot, "Blocks", "A.xml")));
    }

    [Fact]
    public async Task BootstrapRequiresVersionControlForInitialBaselineCommit()
    {
        var fixture = Fixture.Create(root, knowledgeStale: true);
        var engineering = new BootstrapExportCaller();
        var versionControl = new FakeToolCaller()
            .Fail("vc_log", "GIT_ERROR", "version control is unavailable");
        var knowledge = new FakeToolCaller()
            .Respond("ingest_source", new IngestResult { DbPath = fixture.Context.KnowledgeDbPath });
        var coordinator = Create(
            fixture,
            engineering: engineering,
            knowledge: knowledge,
            versionControl: versionControl);

        await Assert.ThrowsAsync<ToolCallException>(() => coordinator.BootstrapDeviceAsync(
            fixture.Context,
            CancellationToken.None));

        Assert.Equal(["vc_log"], versionControl.Calls);
        Assert.Empty(knowledge.Calls);
    }

    [Fact]
    public async Task DeleteWorkbenchRemovesWorktreesDeletesRootAndDropsRegistrations()
    {
        var catalog = new WorkbenchCatalog(new AtomicJsonStore(), Path.Combine(root, "catalog"));
        var workbench = catalog.Create("Line", Path.Combine(root, "Line"));
        workbench = catalog.RegisterWorktree(
            workbench,
            new WorkbenchWorktreeRegistration("wt-1", "master", "master", "master"));
        workbench = catalog.RegisterWorktree(
            workbench,
            new WorkbenchWorktreeRegistration("wt-2", "feature", "feature", "feature"));
        var masterRoot = Path.Combine(workbench.RootPath, "worktrees", "master");
        var featureRoot = Path.Combine(workbench.RootPath, "worktrees", "feature");
        Directory.CreateDirectory(masterRoot);
        Directory.CreateDirectory(featureRoot);
        Directory.CreateDirectory(workbench.RepositoryPath);
        var versionControl = new FakeToolCaller()
            .Respond("vc_remove_worktree", new object())
            .Respond("vc_remove_worktree", new object());
        var coordinator = new WorkbenchCoordinator(
            new FakeToolCaller(),
            new FakeToolCaller(),
            versionControl,
            catalog,
            new AtomicJsonStore(),
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        coordinator.RegisterWorkbench(workbench);

        await coordinator.DeleteWorkbenchAsync(workbench, CancellationToken.None);

        Assert.Equal(new[] { "vc_remove_worktree", "vc_remove_worktree" }, versionControl.Calls);
        var removals = versionControl.CallArgs["vc_remove_worktree"]
            .Select(args => Property<string>(args, "worktreePath"))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { featureRoot, masterRoot }.Order(StringComparer.Ordinal), removals);
        Assert.All(
            versionControl.CallArgs["vc_remove_worktree"],
            args => Assert.Equal(workbench.RepositoryPath, Property<string>(args, "repositoryPath")));
        Assert.False(Directory.Exists(workbench.RootPath));
        var missing = Assert.Throws<WorkbenchCatalogException>(() => catalog.Load(workbench.RootPath));
        Assert.Equal("WORKBENCH_NOT_FOUND", missing.Code);
    }

    [Fact]
    public async Task DeleteWorkbenchRejectsIdentityMismatchAndPreservesDirectory()
    {
        var catalog = new WorkbenchCatalog(new AtomicJsonStore(), Path.Combine(root, "catalog"));
        var workbench = catalog.Create("Line", Path.Combine(root, "Line"));
        var versionControl = new FakeToolCaller();
        var coordinator = new WorkbenchCoordinator(
            new FakeToolCaller(),
            new FakeToolCaller(),
            versionControl,
            catalog,
            new AtomicJsonStore(),
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));

        var error = await Assert.ThrowsAsync<WorkbenchCatalogException>(() =>
            coordinator.DeleteWorkbenchAsync(
                workbench with { WorkbenchId = "foreign-id" },
                CancellationToken.None));

        Assert.Equal("WORKBENCH_RELATIONSHIP_MISMATCH", error.Code);
        Assert.Empty(versionControl.Calls);
        Assert.True(Directory.Exists(workbench.RootPath));
        Assert.True(File.Exists(Path.Combine(workbench.RootPath, "workbench.json")));
    }

    [Fact]
    public async Task DeleteWorkbenchToleratesAlreadyMissingCheckout()
    {
        var catalog = new WorkbenchCatalog(new AtomicJsonStore(), Path.Combine(root, "catalog"));
        var workbench = catalog.Create("Line", Path.Combine(root, "Line"));
        workbench = catalog.RegisterWorktree(
            workbench,
            new WorkbenchWorktreeRegistration("wt-1", "master", "master", "master"));
        Directory.CreateDirectory(workbench.RepositoryPath);
        // The master checkout was deleted manually before the workbench delete.
        var versionControl = new FakeToolCaller()
            .Fail("vc_remove_worktree", "WORKTREE_REMOVE_FAILED", "not a registered worktree");
        var coordinator = new WorkbenchCoordinator(
            new FakeToolCaller(),
            new FakeToolCaller(),
            versionControl,
            catalog,
            new AtomicJsonStore(),
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));

        await coordinator.DeleteWorkbenchAsync(workbench, CancellationToken.None);

        Assert.Equal(new[] { "vc_remove_worktree" }, versionControl.Calls);
        Assert.False(Directory.Exists(workbench.RootPath));
    }

    [Fact]
    public async Task DeleteWorkbenchToleratesCheckoutDirectoryThatIsNotARegisteredWorktree()
    {
        var catalog = new WorkbenchCatalog(new AtomicJsonStore(), Path.Combine(root, "catalog"));
        var workbench = catalog.Create("Line", Path.Combine(root, "Line"));
        workbench = catalog.RegisterWorktree(
            workbench,
            new WorkbenchWorktreeRegistration("wt-1", "master", "master", "master"));
        Directory.CreateDirectory(workbench.RepositoryPath);
        Directory.CreateDirectory(Path.Combine(workbench.RootPath, "worktrees", "master"));

        var versionControl = new FakeToolCaller()
            .Fail("vc_remove_worktree", "WORKTREE_REMOVE_FAILED", "fatal: 'master' is not a working tree");
        var coordinator = new WorkbenchCoordinator(
            new FakeToolCaller(),
            new FakeToolCaller(),
            versionControl,
            catalog,
            new AtomicJsonStore(),
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));

        await coordinator.DeleteWorkbenchAsync(workbench, CancellationToken.None);

        Assert.Equal(new[] { "vc_remove_worktree" }, versionControl.Calls);
        Assert.False(Directory.Exists(workbench.RootPath));
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
            tool.StartsWith("vc_", StringComparison.Ordinal)
            || tool.StartsWith("svn_", StringComparison.Ordinal) ? "version"
            : tool is "update_components" or "ingest_source" ? "knowledge"
            : "engineering";
    }

    /// <summary>Scripts the 1.2 create flow on an engineering caller: connect, origin
    /// get_project_info, save_project_as into the tia/ store, managed verify, per-PLC
    /// compile_plc + get_plc_checksums, rebuild_export per PLC, disconnect.</summary>
    private static FakeToolCaller ScriptCreateEngineering(
        FakeToolCaller caller,
        string[] plcs,
        string originPath = @"C:\Projects\Line.ap17",
        string compileState = "success")
    {
        string? managedPath = null;
        caller
            .Respond("connect", new object())
            .Respond("get_project_info", new ProjectInfo
            {
                Name = "Line",
                Path = originPath,
                PlcDevices = plcs,
            })
            .Respond("save_project_as", args =>
            {
                var target = Property<string>(args, "targetDirectory");
                Directory.CreateDirectory(target);
                managedPath = Path.Combine(target, "Line.ap17");
                File.WriteAllText(managedPath, "managed project placeholder");
                return new CoordinatorSaveProjectAsResult { ManagedProjectPath = managedPath };
            })
            .Respond("get_project_info", _ => new ProjectInfo
            {
                Name = "Line",
                Path = managedPath,
                PlcDevices = plcs,
            });
        foreach (var plc in plcs)
        {
            caller.Respond("compile_plc", new CompileResult { State = compileState });
        }

        caller.Respond("get_plc_checksums", plcs
            .Select(plc => new PlcChecksumInfo { PlcName = plc, SoftwareChecksum = $"checksum-{plc}" })
            .ToArray());
        foreach (var plc in plcs)
        {
            caller.Respond("rebuild_export", (Func<object, object>)WriteCreateExport);
        }

        return caller.Respond("disconnect", new object());
    }

    /// <summary>Scripts the version-control side of the 1.2 create flow.</summary>
    private static FakeToolCaller ScriptCreateVersionControl(FakeToolCaller caller) =>
        caller
            .Respond("vc_init_shared", new object())
            .Respond("svn_init_shared", new CoordinatorSvnInitResult
            {
                RepositoryPath = "repository.svn",
                RepositoryUri = "file:///repository.svn/",
            })
            .Respond("svn_checkout", new object())
            .Respond("svn_commit", new CoordinatorSvnCommitResult { Committed = true, Revision = 1 })
            .Respond("vc_commit_selected", new WorkbenchCommitResult(
                "baseline-sha",
                "Initial PLC source baseline",
                new[] { EngineeringStateWriter.RelativePath }));

    private static object WriteCreateExport(object args)
    {
        var output = Property<string>(args, "outputDir");
        var plc = Property<string>(args, "plcName");
        Directory.CreateDirectory(Path.Combine(output, "Blocks"));
        File.WriteAllText(Path.Combine(output, "Blocks", "Main.xml"), $"<block plc=\"{plc}\" />");
        File.WriteAllText(
            Path.Combine(output, "metadata.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                exportStartedUtc = "2026-08-06T00:00:00Z",
                exportFinishedUtc = "2026-08-06T00:00:01Z",
                exportRoot = output,
                components = new[]
                {
                    new
                    {
                        name = "Main",
                        sourcePath = "Program blocks/Main",
                        category = "OB",
                        status = "Exported",
                        exportedFile = "Blocks/Main.xml",
                    },
                },
            }));
        return new[] { new SyncResult { PlcName = plc, ExportRoot = output } };
    }

    private sealed class MetadataRemovingWorktreeCaller(string masterDevicePath) : FakeToolCaller
    {
        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (!CallArgs.TryGetValue(tool, out var arguments))
            {
                arguments = [];
                CallArgs[tool] = arguments;
            }

            arguments.Add(args);
            cancellationToken.ThrowIfCancellationRequested();
            if (tool == "vc_add_worktree")
            {
                File.Delete(masterDevicePath);
            }

            return Task.FromResult((T)CreateFlowResult(tool));
        }

        internal static object CreateFlowResult(string tool) => tool switch
        {
            "svn_init_shared" => new CoordinatorSvnInitResult
            {
                RepositoryPath = "repository.svn",
                RepositoryUri = "file:///repository.svn/",
            },
            "svn_commit" => new CoordinatorSvnCommitResult { Committed = true, Revision = 1 },
            "vc_commit_selected" => new WorkbenchCommitResult(
                "baseline-sha",
                "Initial PLC source baseline",
                new[] { EngineeringStateWriter.RelativePath }),
            _ => new object(),
        };
    }

    private sealed class PartialWorktreeCreationCaller : FakeToolCaller
    {
        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (!CallArgs.TryGetValue(tool, out var arguments))
            {
                arguments = [];
                CallArgs[tool] = arguments;
            }

            arguments.Add(args);
            cancellationToken.ThrowIfCancellationRequested();
            if (tool == "vc_add_worktree")
            {
                throw new ToolCallException(
                    "GIT_PARTIAL",
                    "linked checkout creation was interrupted",
                    null);
            }

            return Task.FromResult((T)MetadataRemovingWorktreeCaller.CreateFlowResult(tool));
        }
    }

    private sealed class HardwareExportCaller : FakeToolCaller
    {
        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (!CallArgs.TryGetValue(tool, out var list))
            {
                list = new List<object>();
                CallArgs[tool] = list;
            }

            list.Add(args);
            if (tool == "get_project_info")
            {
                return Task.FromResult((T)(object)new ProjectInfo
                {
                    Name = "Line",
                    Path = @"C:\Projects\Line.ap17",
                });
            }

            if (tool == "export_hardware_configuration")
            {
                var output = Property<string>(args, "outputDir");
                Directory.CreateDirectory(output);
                var projectAml = Path.Combine(output, "project.aml");
                File.WriteAllText(projectAml, "<CAEXFile />");
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

    private sealed class RecordingProgress : IOperationProgress
    {
        public List<string> Messages { get; } = [];
        public void Report(string message) => Messages.Add(message);
    }

    private sealed class BootstrapExportCaller(List<string>? order = null) : FakeToolCaller
    {
        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            CallArgs[tool] = new List<object> { args };
            order?.Add($"engineering:{tool}");
            var output = Property<string>(args, "outputDir");
            Directory.CreateDirectory(Path.Combine(output, "Blocks"));
            File.WriteAllText(Path.Combine(output, "Blocks", "A.xml"), "<a />");
            File.WriteAllText(
                Path.Combine(output, "metadata.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    exportStartedUtc = "2026-07-30T00:00:00Z",
                    exportFinishedUtc = "2026-07-30T00:00:01Z",
                    exportRoot = output,
                    components = new[]
                    {
                        new
                        {
                            name = "A",
                            sourcePath = "Program blocks/A",
                            category = "FC",
                            status = "Exported",
                            exportedFile = "Blocks/A.xml",
                        },
                    },
                }));
            return Task.FromResult((T)(object)new[]
            {
                new SyncResult { PlcName = "PLC_1", ExportRoot = output },
            });
        }
    }

    private sealed class MultiDeviceBootstrapExportCaller(List<string>? order = null) : FakeToolCaller
    {
        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (!CallArgs.TryGetValue(tool, out var list))
            {
                list = new List<object>();
                CallArgs[tool] = list;
            }

            list.Add(args);
            order?.Add($"engineering:{tool}");
            if (tool != "rebuild_export")
                throw new InvalidOperationException(tool);

            var output = Property<string>(args, "outputDir");
            var plcName = Property<string>(args, "plcName");
            Directory.CreateDirectory(Path.Combine(output, "Blocks"));
            File.WriteAllText(Path.Combine(output, "Blocks", "A.xml"), $"<{plcName} />");
            File.WriteAllText(
                Path.Combine(output, "metadata.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    exportStartedUtc = "2026-07-30T00:00:00Z",
                    exportFinishedUtc = "2026-07-30T00:00:01Z",
                    exportRoot = output,
                    components = new[]
                    {
                        new
                        {
                            name = "A",
                            sourcePath = "Program blocks/A",
                            category = "FC",
                            status = "Exported",
                            exportedFile = "Blocks/A.xml",
                        },
                    },
                }));
            return Task.FromResult((T)(object)new[]
            {
                new SyncResult { PlcName = plcName, ExportRoot = output },
            });
        }
    }

    /// <summary>First rebuild_export fails with an inconsistent block; compile_plc succeeds;
    /// the retried export stages the same manifest + file as <see cref="BootstrapExportCaller"/>.</summary>
    private sealed class CompileRetryBootstrapCaller : FakeToolCaller
    {
        private int exportAttempts;

        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (!CallArgs.TryGetValue(tool, out var list))
            {
                list = new List<object>();
                CallArgs[tool] = list;
            }

            list.Add(args);
            if (tool == "compile_plc")
            {
                return Task.FromResult((T)(object)new CompileResult { State = "success" });
            }

            var output = Property<string>(args, "outputDir");
            if (++exportAttempts == 1)
            {
                Directory.CreateDirectory(output);
                return Task.FromResult((T)(object)new[]
                {
                    new SyncResult
                    {
                        PlcName = "PLC_1",
                        Failed = new[]
                        {
                            new SyncChange
                            {
                                Name = "A",
                                Category = "FC",
                                Reason = "Block 'A' is inconsistent. Compile it first before export.",
                            },
                        },
                    },
                });
            }

            Directory.CreateDirectory(Path.Combine(output, "Blocks"));
            File.WriteAllText(Path.Combine(output, "Blocks", "A.xml"), "<a />");
            File.WriteAllText(
                Path.Combine(output, "metadata.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    exportStartedUtc = "2026-07-30T00:00:00Z",
                    exportFinishedUtc = "2026-07-30T00:00:01Z",
                    exportRoot = output,
                    components = new[]
                    {
                        new
                        {
                            name = "A",
                            sourcePath = "Program blocks/A",
                            category = "FC",
                            status = "Exported",
                            exportedFile = "Blocks/A.xml",
                        },
                    },
                }));
            return Task.FromResult((T)(object)new[]
            {
                new SyncResult { PlcName = "PLC_1", ExportRoot = output },
            });
        }
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

    private sealed class CompileRetryExportCaller : FakeToolCaller
    {
        private int exportAttempts;

        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (!CallArgs.TryGetValue(tool, out var list))
            {
                list = new List<object>();
                CallArgs[tool] = list;
            }

            list.Add(args);
            if (tool == "compile_plc")
            {
                return Task.FromResult((T)(object)new CompileResult
                {
                    State = "success",
                });
            }

            if (tool == "rebuild_export")
            {
                exportAttempts++;
                var output = Property<string>(args, "outputDir");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "metadata.json"), "{}");
                if (exportAttempts == 1)
                {
                    return Task.FromResult((T)(object)new[]
                    {
                        new SyncResult
                        {
                            PlcName = "PLC_1",
                            Failed = new[]
                            {
                                new SyncChange
                                {
                                    Name = "A",
                                    Category = "FC",
                                    Reason = "Block 'A' is inconsistent. Compile it first before export.",
                                },
                            },
                        },
                    });
                }

                return Task.FromResult((T)(object)new[]
                {
                    new SyncResult { PlcName = "PLC_1", ExportRoot = output },
                });
            }

            throw new InvalidOperationException(tool);
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
        private string? managedPath;
        private int projectInfoCalls;

        public override async Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            switch (tool)
            {
                case "connect":
                case "disconnect":
                    return (T)(object)new object();
                case "get_project_info":
                    projectInfoCalls++;
                    if (projectInfoCalls == 1)
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

                    return (T)(object)new ProjectInfo
                    {
                        Name = "Created",
                        Path = managedPath,
                        PlcDevices = ["PLC_1"],
                    };
                case "save_project_as":
                {
                    var target = Property<string>(args, "targetDirectory");
                    Directory.CreateDirectory(target);
                    managedPath = Path.Combine(target, "Created.ap17");
                    File.WriteAllText(managedPath, "managed project placeholder");
                    return (T)(object)new CoordinatorSaveProjectAsResult
                    {
                        ManagedProjectPath = managedPath,
                    };
                }
                case "compile_plc":
                    return (T)(object)new CompileResult { State = "success" };
                case "get_plc_checksums":
                    return (T)(object)new[]
                    {
                        new PlcChecksumInfo { PlcName = "PLC_1", SoftwareChecksum = "checksum-PLC_1" },
                    };
                case "rebuild_export":
                    return (T)WriteCreateExport(args);
                default:
                    throw new InvalidOperationException(tool);
            }
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
            Directory.CreateDirectory(context.SourceRoot);
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
            Write(Context.SourceRoot, relative, content);
        public void WriteStaging(string relative, string content) =>
            Write(Context.StagingRoot, relative, content);
        public void WriteModified(string relative, string content) =>
            Write(Context.SourceRoot, relative, content);

        public DeviceContext AddDevice(string deviceId, string plcName)
        {
            var context = new DeviceContext(
                Context.WorkbenchId,
                Context.WorktreeId,
                deviceId,
                Context.WorkbenchRoot,
                Context.WorktreeRoot,
                Path.Combine(Context.WorktreeRoot, "devices", plcName),
                Path.Combine(Context.WorktreeRoot, "devices", plcName, "source"),
                Path.Combine(Context.WorktreeRoot, "devices", plcName, "staging"),
                Path.Combine(Context.WorktreeRoot, "devices", plcName, "plc-knowledge.db"));
            Directory.CreateDirectory(context.SourceRoot);
            Directory.CreateDirectory(context.StagingRoot);
            store.Write(
                Path.Combine(context.DeviceRoot, "device.json"),
                new DeviceMetadata(
                    WorkbenchSchema.CurrentVersion,
                    deviceId,
                    Context.WorktreeId,
                    plcName,
                    plcName,
                    null,
                    null,
                    null,
                    new KnowledgeState(true, new Dictionary<string, string>(), null),
                    Array.Empty<DeviceImportRecord>()));
            var worktreePath = Path.Combine(Context.WorktreeRoot, "worktree.json");
            var worktree = store.Read<WorktreeMetadata>(worktreePath);
            store.Write(worktreePath, worktree with { DeviceIds = worktree.DeviceIds.Append(deviceId).ToArray() });
            return context;
        }

        public void WriteManifests(params string[] staging)
        {
            WriteManifest(Context.StagingRoot, staging);
            var baseline = Directory.EnumerateFiles(
                    Context.SourceRoot, "*.xml", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Context.SourceRoot, path).Replace('\\', '/'))
                .ToArray();
            if (baseline.Length > 0)
            {
                WriteManifest(Context.SourceRoot, baseline);
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
