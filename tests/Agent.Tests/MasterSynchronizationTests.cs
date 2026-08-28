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
    public async Task ApplyCopiesSelectedChangedObjectsAndAutoCommitsWithTheProvidedTitle()
    {
        var coordinator = fixture.CreateCoordinator();

        var result = await coordinator.ApplyTiaSynchronizationAsync(
            fixture.Workbench.WorkbenchId,
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
    public async Task MasterCommitAllowsADirectLocalEdit()
    {
        var coordinator = fixture.CreateCoordinator();
        await coordinator.ApplyTiaSynchronizationAsync(
            fixture.Workbench.WorkbenchId,
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
                    new SourceDifference("device-1", "PLC_1", "devices/PLC_1/source/Blocks/A.xml", "Blocks:A", SourceDifferenceKind.Changed, "old", "new", true),
                    new SourceDifference("device-1", "PLC_1", "devices/PLC_1/source/Blocks/B.xml", "Blocks:B", SourceDifferenceKind.Changed, "old", "new", true),
                });
            store.Write(System.IO.Path.Combine(root, ".automation", "comparisons", "comparison-1.json"), comparison);
            return new SyncFixture(root, workbench, master, store, new SyncVersionControlCaller());
        }

        public string Path(string relative) => $"devices/PLC_1/source/{relative}";

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
            throw new InvalidOperationException(tool);
    }

    private sealed class SyncVersionControlCaller : IMcpToolCaller
    {
        public List<string> Calls { get; } = new();
        public string Head { get; private set; } = "head-1";
        public string? CommitMessage { get; private set; }
        public object? CommitArgs { get; private set; }
        public object? StateCreateArgs { get; private set; }

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
            if (tool == "vc_commit_state_create")
            {
                StateCreateArgs = args;
                return Task.FromResult((T)(object)new object());
            }
            throw new InvalidOperationException(tool);
        }
    }
}
