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
    public async Task ApplyCopiesOnlySelectedChangedObjectsAndDoesNotCommit()
    {
        var coordinator = fixture.CreateCoordinator();

        var result = await coordinator.ApplyTiaSynchronizationAsync(
            fixture.Workbench.WorkbenchId,
            fixture.ComparisonId,
            [fixture.Path("Blocks/A.xml")],
            CancellationToken.None);

        Assert.Equal([fixture.Path("Blocks/A.xml")], result.PendingPaths);
        Assert.Equal("new A", File.ReadAllText(fixture.MasterSource("Blocks/A.xml")));
        Assert.Equal("old B", File.ReadAllText(fixture.MasterSource("Blocks/B.xml")));
        Assert.DoesNotContain("vc_commit_selected", fixture.VersionControl.Calls);
    }

    [Fact]
    public async Task MasterCommitRejectsAFileChangedAfterAuthorization()
    {
        var coordinator = fixture.CreateCoordinator();
        await coordinator.ApplyTiaSynchronizationAsync(
            fixture.Workbench.WorkbenchId,
            fixture.ComparisonId,
            [fixture.Path("Blocks/A.xml")],
            CancellationToken.None);
        File.WriteAllText(fixture.MasterSource("Blocks/A.xml"), "local edit");

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
            coordinator.CommitSourceAsync(
                fixture.Workbench.WorkbenchId,
                fixture.Master.WorktreeId,
                [fixture.Path("Blocks/A.xml")],
                "accept A",
                CancellationToken.None));

        Assert.Equal("MASTER_CHANGE_NOT_AUTHORIZED", error.Code);
    }

    public void Dispose() => fixture.Dispose();

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

        public static SyncFixture Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "master-sync-tests", Guid.NewGuid().ToString("N"));
            var store = new AtomicJsonStore();
            var workbench = new WorkbenchMetadata(
                "1.0", "wb-1", "wb", "now", root, System.IO.Path.Combine(root, "repository.git"), "project-1", null,
                new[] { new WorkbenchWorktreeRegistration("master-1", "master", "master", "master") });
            var master = new WorktreeMetadata(
                "1.0", "master-1", "wb-1", "master", "master", "now", "head-1", "project-1", null,
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

        public WorkbenchCoordinator CreateCoordinator()
        {
            var catalog = new WorkbenchCatalog(Store, System.IO.Path.Combine(Root, "catalog"));
            var coordinator = new WorkbenchCoordinator(
                new NoOpCaller(),
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
                Head = "head-2";
                return Task.FromResult((T)(object)new { committed = true });
            }
            throw new InvalidOperationException(tool);
        }
    }
}
