using System.Text.Json;
using System.Security.Cryptography;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Contracts.Engineering;
using Contracts.Knowledge;
using Mcp.Knowledge.Tools;
using Mcp.VersionControl.Git;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using Xunit;

namespace E2E.Tests;

public sealed class WorkbenchLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"workbench-e2e-{Guid.NewGuid():N}");

    [Fact]
    public async Task PersistedDeviceRemainsUsableAfterEngineeringDisconnectAndApiRestart()
    {
        var fixture = await OfflineRestartFixture.CreateAsync(root);
        var databaseHash = SHA256.HashData(
            File.ReadAllBytes(fixture.Device.Context.KnowledgeDbPath));

        fixture.DisconnectEngineering();
        var snapshot = fixture.RestartAndReadPersistedSnapshot();
        var otherSnapshot = fixture.RestartAndReadPersistedSnapshot(fixture.OtherDevice);

        Assert.NotEmpty(snapshot.Blocks);
        Assert.Equal("current", snapshot.Knowledge.State);
        Assert.Equal(fixture.Device.Context.DeviceId, snapshot.DeviceId);
        Assert.Equal(fixture.Device.Context.WorktreeId, snapshot.WorktreeId);
        Assert.Equal(fixture.Device.Context.ExportedSourceRoot, snapshot.ExportedSourceRoot);
        Assert.Equal(fixture.OtherDevice.Context.DeviceId, otherSnapshot.DeviceId);
        Assert.Equal(fixture.OtherDevice.Context.WorktreeId, otherSnapshot.WorktreeId);
        Assert.NotEqual(snapshot.ExportedSourceRoot, otherSnapshot.ExportedSourceRoot);
        Assert.NotEqual(snapshot.KnowledgeDbPath, otherSnapshot.KnowledgeDbPath);
        Assert.Equal(
            databaseHash,
            SHA256.HashData(File.ReadAllBytes(fixture.Device.Context.KnowledgeDbPath)));
        Assert.Empty(fixture.EngineeringCallsAfterRestart);
    }

    [Fact]
    public async Task CoordinatorDrivesApprovedLifecycleAcrossDevicesAndLinkedWorktrees()
    {
        var defaultParent = Path.Combine(root, "AutomationWorkbench", "Project");
        var legacySentinel = Path.Combine(root, "PlcAiAssistant", "exports", "legacy", "sentinel.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(legacySentinel)!);
        File.WriteAllText(legacySentinel, "do-not-touch");

        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, defaultParent);
        var engineering = new EngineeringBoundary();
        var git = new GitBoundary();
        var knowledge = new KnowledgeBoundary();
        var resolver = new DeviceSourceResolver(device =>
        {
            var path = Path.Combine(device.DeviceRoot, "device.json");
            var metadata = store.Read<DeviceMetadata>(path);
            store.Write(path, metadata with
            {
                Knowledge = metadata.Knowledge with { Stale = true },
            });
        });
        var coordinator = new WorkbenchCoordinator(
            engineering, knowledge, git, catalog, store,
            new DeviceReconciler(), resolver);

        var created = await coordinator.CreateWorkbenchAsync(new(
            "Line 1", Path.Combine(root, "custom", "Line 1"), 42, null));
        Assert.Empty(catalog.ListDefaultRoot());
        Assert.Equal("do-not-touch", File.ReadAllText(legacySentinel));

        var plc1 = catalog.ResolveDevice(created.Workbench, created.Worktree, created.Devices[0]);
        var plc2 = catalog.ResolveDevice(created.Workbench, created.Worktree, created.Devices[1]);
        SetKnowledgeCurrent(store, plc1);
        SetKnowledgeCurrent(store, plc2);
        engineering.SetExport("PLC_1", "v1");
        engineering.SetExport("PLC_2", "v1");

        await coordinator.StageRefreshAsync(plc1, CancellationToken.None);
        var rejectedPreview = coordinator.PreviewRefresh(plc1);
        var rejected = await coordinator.ApplyRefreshAsync(
            plc1, ApprovedReconciliation.Rejected(rejectedPreview), CancellationToken.None);
        Assert.Equal(RefreshApplyState.Rejected, rejected.State);
        Assert.False(File.Exists(Path.Combine(plc1.ExportedSourceRoot, "Blocks", "Main.xml")));
        Assert.Empty(RepositoryService.Log(plc1.WorktreeRoot, 10).Commits);

        engineering.SetExport("PLC_1", "stale-preview");
        await coordinator.StageRefreshAsync(plc1, CancellationToken.None);
        File.AppendAllText(
            Path.Combine(plc1.StagingRoot, "Blocks", "Main.xml"),
            Environment.NewLine);
        await Assert.ThrowsAsync<ReconciliationException>(() =>
            coordinator.ApplyRefreshAsync(
                plc1, new(rejectedPreview, new HashSet<string>()), CancellationToken.None));

        await coordinator.StageRefreshAsync(plc1, CancellationToken.None);
        var plc1Preview = coordinator.PreviewRefresh(plc1);
        var plc1Apply = await coordinator.ApplyRefreshAsync(
            plc1, ApproveAllChanges(plc1Preview), CancellationToken.None);
        Assert.Equal(RefreshApplyState.Committed, plc1Apply.State);
        Assert.NotNull(plc1Apply.CommitSha);
        Assert.Equal(
            plc1Apply.ChangedPaths.Order(),
            git.AddedPathBatches.Single().Order());
        Assert.True(ReadDevice(store, plc1).Knowledge.BaselineStale);
        Assert.False(ReadDevice(store, plc2).Knowledge.BaselineStale);

        await coordinator.StageRefreshAsync(plc2, CancellationToken.None);
        var plc2Preview = coordinator.PreviewRefresh(plc2);
        var plc2Apply = await coordinator.ApplyRefreshAsync(
            plc2, ApproveAllChanges(plc2Preview),
            CancellationToken.None);
        Assert.Equal(RefreshApplyState.Committed, plc2Apply.State);
        var baselineCommitCount = RepositoryService.Log(plc1.WorktreeRoot, 20).Commits.Length;

        await coordinator.UpdateKnowledgeAsync(plc1, CancellationToken.None);
        await coordinator.UpdateKnowledgeAsync(plc2, CancellationToken.None);
        Assert.False(ReadDevice(store, plc1).Knowledge.Stale);
        Assert.False(ReadDevice(store, plc1).Knowledge.BaselineStale);
        Assert.False(ReadDevice(store, plc2).Knowledge.Stale);
        Assert.NotEqual(plc1.KnowledgeDbPath, plc2.KnowledgeDbPath);
        SqliteConnection.ClearAllPools();
        var plc2DbHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(plc2.KnowledgeDbPath)));

        var baselineFile = Path.Combine(plc1.ExportedSourceRoot, "Blocks", "Main.xml");
        var baselineHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(baselineFile)));
        var baselineTimestamp = File.GetLastWriteTimeUtc(baselineFile);
        engineering.SetExport("PLC_1", "stale-preview");
        await coordinator.StageRefreshAsync(plc1, CancellationToken.None);
        var noOp = await coordinator.ApplyRefreshAsync(
            plc1, new(coordinator.PreviewRefresh(plc1), new HashSet<string>()),
            CancellationToken.None);
        Assert.Empty(noOp.ChangedPaths);
        Assert.Equal(baselineCommitCount, RepositoryService.Log(plc1.WorktreeRoot, 20).Commits.Length);
        Assert.Equal(baselineTimestamp, File.GetLastWriteTimeUtc(baselineFile));
        Assert.Equal(baselineHash, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(baselineFile))));

        var overlay = resolver.PrepareEditable(plc1, "Blocks/Main.xml");
        File.AppendAllText(overlay, Environment.NewLine);
        Assert.True(ReadDevice(store, plc1).Knowledge.Stale);
        Assert.False(ReadDevice(store, plc2).Knowledge.Stale);
        var partialBefore = knowledge.PartialCalls;
        await coordinator.UpdateKnowledgeAsync(plc1, CancellationToken.None);
        Assert.Equal(partialBefore + 1, knowledge.PartialCalls);
        Assert.False(ReadDevice(store, plc1).Knowledge.Stale);
        SqliteConnection.ClearAllPools();
        Assert.Equal(plc2DbHash, Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(plc2.KnowledgeDbPath))));

        var imported = await coordinator.ImportModifiedAsync(
            plc1, "Blocks/Main.xml", CancellationToken.None);
        Assert.True(imported.ImportSucceeded);
        Assert.Equal("success", imported.CompileState);
        Assert.Equal(new[] { "import_block", "compile_block" }, engineering.ImportCalls);
        Assert.True(File.Exists(overlay));

        engineering.SetExport("PLC_1", "post-edit-refresh");
        await coordinator.StageRefreshAsync(plc1, CancellationToken.None);
        var postEditPreview = coordinator.PreviewRefresh(plc1);
        var postEditRefresh = await coordinator.ApplyRefreshAsync(
            plc1, ApproveAllChanges(postEditPreview),
            CancellationToken.None);
        Assert.Equal(RefreshApplyState.Committed, postEditRefresh.State);
        Assert.NotNull(postEditRefresh.CommitSha);
        Assert.True(File.Exists(overlay));
        Assert.True(ReadDevice(store, plc1).Knowledge.BaselineStale);

        var session = SessionManager.CreateNewSession(plc1, new ChatRequestSettings(), "context");
        session.Messages.Add(ChatMessage.User("hello"));
        SessionManager.SaveSession(plc1, session);
        Assert.Equal("hello", SessionManager.LoadSession(plc1, session.Header.SessionId)!.Messages.Single().Content);
        Assert.Contains(".automation/sessions", SessionManager.ResolveSessionPath(plc1, session.Header.SessionId)!.Replace('\\', '/'));
        var sessionStatus = RepositoryService.Status(plc1.WorktreeRoot).Entries.Single(
            entry => entry.FilePath.Contains(".automation", StringComparison.Ordinal));
        Assert.Equal("Ignored", sessionStatus.State);
        Assert.False(sessionStatus.Staged);

        RepositoryService.Add(plc1.WorktreeRoot, [
            ".gitignore",
            Path.GetRelativePath(plc1.WorktreeRoot, overlay).Replace('\\', '/'),
            Path.GetRelativePath(
                plc1.WorktreeRoot, Path.Combine(plc1.DeviceRoot, "device.json")).Replace('\\', '/'),
            Path.GetRelativePath(
                plc1.WorktreeRoot, Path.Combine(plc2.DeviceRoot, "device.json")).Replace('\\', '/'),
            "worktree.json",
        ]);
        var retainedOverlayCommit = RepositoryService.Commit(
            plc1.WorktreeRoot, "retain PLC_1 worktree overlay", null);

        var feature = await coordinator.CreateWorktreeAsync(new(
            catalog.Load(created.Workbench.RootPath), "feature-a", "feature-a", retainedOverlayCommit.Sha));
        var currentWorkbench = catalog.Load(created.Workbench.RootPath);
        var featureRegistration = currentWorkbench.Worktrees.Single(item => item.WorktreeId == feature.WorktreeId);
        var featureRoot = WorkbenchPaths.ResolveWorktree(currentWorkbench.RootPath, featureRegistration.RelativePath);
        AssertCompleteDeviceTrees(featureRoot, "PLC_1", "PLC_2");

        var featureOverlay = Path.Combine(featureRoot, Path.GetRelativePath(plc1.WorktreeRoot, overlay));
        File.AppendAllText(featureOverlay, Environment.NewLine);
        RepositoryService.Add(featureRoot, [Path.GetRelativePath(featureRoot, featureOverlay).Replace('\\', '/')]);
        var featureCommit = RepositoryService.Commit(featureRoot, "feature overlay", null);
        var dirtyTarget = RepositoryService.Status(plc1.WorktreeRoot).Entries
            .Where(entry => entry.State != "Ignored")
            .Select(entry => $"{entry.FilePath}:{entry.State}")
            .ToArray();
        Assert.True(dirtyTarget.Length == 0, string.Join(", ", dirtyTarget));
        await coordinator.MergeWorktreeAsync(
            currentWorkbench.WorkbenchId, feature.WorktreeId, created.Worktree.WorktreeId);

        Assert.Contains(
            RepositoryService.Log(plc1.WorktreeRoot, 20).Commits,
            commit => commit.Sha == featureCommit.Sha);
        AssertCompleteDeviceTrees(plc1.WorktreeRoot, "PLC_1", "PLC_2");
        Assert.Equal("do-not-touch", File.ReadAllText(legacySentinel));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void DefaultCatalogRootIsInjectedAndTraversalIsRejected()
    {
        var defaults = Path.Combine(root, "AutomationWorkbench", "Project");
        var catalog = new WorkbenchCatalog(new AtomicJsonStore(), defaults);
        var workbench = catalog.Create("Line:1", null);
        Assert.Equal(Path.Combine(defaults, "Line_1"), workbench.RootPath);
        Assert.Throws<WorkbenchPathException>(() =>
            WorkbenchPaths.ResolveRelative(workbench.RootPath, "../escape"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(root))
            return;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(root, true);
    }

    private static DeviceMetadata ReadDevice(AtomicJsonStore store, DeviceContext device) =>
        store.Read<DeviceMetadata>(Path.Combine(device.DeviceRoot, "device.json"));

    private static void SetKnowledgeCurrent(AtomicJsonStore store, DeviceContext device)
    {
        var path = Path.Combine(device.DeviceRoot, "device.json");
        var metadata = store.Read<DeviceMetadata>(path);
        store.Write(path, metadata with
        {
            Knowledge = new KnowledgeState(false, new Dictionary<string, string>(), null, false),
        });
    }

    private static void AssertCompleteDeviceTrees(string worktreeRoot, params string[] devices)
    {
        foreach (var device in devices)
        {
            var root = Path.Combine(worktreeRoot, "devices", device);
            Assert.True(File.Exists(Path.Combine(root, "device.json")));
            Assert.True(File.Exists(Path.Combine(root, "exported-source", "Blocks", "Main.xml")));
            Assert.True(File.Exists(Path.Combine(root, "exported-source", "metadata.json")));
        }
    }

    private static T Property<T>(object args, string name) =>
        (T)args.GetType().GetProperty(name)!.GetValue(args)!;

    private static ApprovedReconciliation ApproveAllChanges(ReconciliationPreview preview) =>
        new(
            preview,
            preview.Entries
                .Where(entry => entry.Kind is not ReconciliationChangeKind.Unchanged)
                .Select(entry => entry.RelativePath)
                .ToHashSet(StringComparer.Ordinal));

    private sealed class OfflineRestartFixture
    {
        private readonly string defaultParent;
        private readonly string workbenchRoot;
        private readonly EngineeringBoundary engineering;
        private readonly int callsAtDisconnect;

        private OfflineRestartFixture(
            string defaultParent,
            string workbenchRoot,
            EngineeringBoundary engineering,
            (DeviceContext Context, DeviceMetadata Metadata) device,
            (DeviceContext Context, DeviceMetadata Metadata) otherDevice)
        {
            this.defaultParent = defaultParent;
            this.workbenchRoot = workbenchRoot;
            this.engineering = engineering;
            Device = device;
            OtherDevice = otherDevice;
            callsAtDisconnect = engineering.Calls.Count;
        }

        public (DeviceContext Context, DeviceMetadata Metadata) Device { get; }
        public (DeviceContext Context, DeviceMetadata Metadata) OtherDevice { get; }
        public IReadOnlyList<string> EngineeringCallsAfterRestart =>
            engineering.Calls.Skip(callsAtDisconnect).ToArray();

        public static async Task<OfflineRestartFixture> CreateAsync(string root)
        {
            var defaultParent = Path.Combine(root, "offline-restart", "catalog");
            var store = new AtomicJsonStore();
            var catalog = new WorkbenchCatalog(store, defaultParent);
            var engineering = new EngineeringBoundary();
            var coordinator = new WorkbenchCoordinator(
                engineering,
                new KnowledgeBoundary(),
                new GitBoundary(),
                catalog,
                store,
                new DeviceReconciler(),
                new DeviceSourceResolver(_ => { }));
            var created = await coordinator.CreateWorkbenchAsync(new(
                "Offline line",
                null,
                42,
                null));

            engineering.SetExport("PLC_1", "persisted");
            engineering.SetExport("PLC_2", "other-device");
            var devices = created.Devices
                .Select(metadata => (
                    Context: catalog.ResolveDevice(created.Workbench, created.Worktree, metadata),
                    Metadata: metadata))
                .ToArray();

            foreach (var device in devices)
            {
                await coordinator.StageRefreshAsync(device.Context, CancellationToken.None);
                var preview = coordinator.PreviewRefresh(device.Context);
                await coordinator.ApplyRefreshAsync(
                    device.Context,
                    ApproveAllChanges(preview),
                    CancellationToken.None);
                await coordinator.UpdateKnowledgeAsync(device.Context, CancellationToken.None);
            }
            SqliteConnection.ClearAllPools();

            return new(
                defaultParent,
                created.Workbench.RootPath,
                engineering,
                devices[0],
                devices[1]);
        }

        public void DisconnectEngineering() => engineering.Disconnect();

        public DeviceSnapshot RestartAndReadPersistedSnapshot() =>
            RestartAndReadPersistedSnapshot(Device);

        public DeviceSnapshot RestartAndReadPersistedSnapshot(
            (DeviceContext Context, DeviceMetadata Metadata) device)
        {
            var restartedStore = new AtomicJsonStore();
            var restartedCatalog = new WorkbenchCatalog(restartedStore, defaultParent);
            var workbench = restartedCatalog.Load(workbenchRoot);
            var worktree = restartedStore.Read<WorktreeMetadata>(
                Path.Combine(workbenchRoot, "worktrees", "master", "worktree.json"));
            var metadata = restartedStore.Read<DeviceMetadata>(
                Path.Combine(
                    workbenchRoot,
                    "worktrees",
                    "master",
                    "devices",
                    device.Metadata.PlcName,
                    "device.json"));
            var selected = restartedCatalog.ResolveDevice(workbench, worktree, metadata);

            return new DeviceSnapshotReader().Read(selected, metadata);
        }
    }

    private sealed class GitBoundary : IMcpToolCaller
    {
        public List<string[]> AddedPathBatches { get; } = [];

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            object result = tool switch
            {
                "vc_init_shared" => RepositoryService.InitShared(
                    Property<string>(args, "workbenchRoot"), Property<string>(args, "masterWorktreePath")),
                "vc_add_worktree" => RepositoryService.AddWorktree(
                    Property<string>(args, "repositoryPath"), Property<string>(args, "worktreePath"),
                    Property<string>(args, "branchName"), Property<string?>(args, "startPoint")),
                "vc_add" => Add(args),
                "vc_commit" => Commit(args),
                "vc_merge" => RepositoryService.Merge(
                    Property<string>(args, "targetWorktreePath"), Property<string>(args, "sourceBranch")),
                _ => throw new InvalidOperationException(tool),
            };
            return Task.FromResult((T)result);
        }

        private object Add(object args)
        {
            var paths = Property<string[]>(args, "paths");
            AddedPathBatches.Add(paths);
            return RepositoryService.Add(Property<string>(args, "repoPath"), paths);
        }

        private static object Commit(object args)
        {
            var commit = RepositoryService.Commit(
                Property<string>(args, "repoPath"), Property<string>(args, "message"), null);
            return new CoordinatorGitCommitResult { Sha = commit.Sha };
        }
    }

    private sealed class EngineeringBoundary : IMcpToolCaller
    {
        private readonly Dictionary<string, string> versions = new(StringComparer.Ordinal);
        private bool disconnected;
        public List<string> ImportCalls { get; } = [];
        public List<string> Calls { get; } = [];

        public void SetExport(string plcName, string version) => versions[plcName] = version;
        public void Disconnect() => disconnected = true;

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (disconnected)
                throw new InvalidOperationException("Engineering is disconnected.");
            object result = tool switch
            {
                "connect" => new object(),
                "get_project_info" => new ProjectInfo
                {
                    Name = "Line",
                    Path = @"C:\Fixture\Line.ap17",
                    PlcDevices = ["PLC_1", "PLC_2"],
                },
                "rebuild_export" => Export(args),
                "import_block" => Import(args),
                "compile_block" => Compile(args),
                _ => throw new InvalidOperationException(tool),
            };
            return Task.FromResult((T)result);
        }

        private SyncResult[] Export(object args)
        {
            var output = Property<string>(args, "outputDir");
            var plc = Property<string>(args, "plcName");
            Directory.CreateDirectory(Path.Combine(output, "Blocks"));
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "Main [OB1].xml"),
                Path.Combine(output, "Blocks", "Main.xml"), true);
            var version = versions[plc];
            File.AppendAllText(
                Path.Combine(output, "Blocks", "Main.xml"),
                $"{Environment.NewLine}<!-- fixture-version:{version} -->{Environment.NewLine}");
            File.WriteAllText(Path.Combine(output, "metadata.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                exportStartedUtc = "2026-07-27T00:00:00Z",
                exportFinishedUtc = "2026-07-27T00:00:01Z",
                exportRoot = "fixture",
                components = new[] { new {
                    id = "main", name = "Main", sourcePath = "Program blocks/Main",
                    category = "OB", folder = "Blocks", siemensTypeName = "OB",
                    status = "Exported", exportedFile = "Blocks/Main.xml",
                    message = (string?)null, programmingLanguage = "LAD",
                    tiaIdentifier = "Main", number = 1, isKnowHowProtected = false,
                    creationDate = version, modifiedDate = version,
                    codeModifiedDate = version, interfaceModifiedDate = version,
                }},
            }));
            return [new SyncResult { PlcName = plc, ExportRoot = output, Status = "updated" }];
        }

        private ImportResult Import(object args)
        {
            ImportCalls.Add("import_block");
            return new ImportResult { BlockName = "Main", Success = true, ImportedAt = DateTime.UtcNow };
        }

        private CompileResult Compile(object args)
        {
            ImportCalls.Add("compile_block");
            return new CompileResult { BlockName = "Main", State = "success" };
        }
    }

    private sealed class KnowledgeBoundary : IMcpToolCaller
    {
        public int PartialCalls { get; private set; }

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            var tools = new KnowledgeTools();
            CallToolResult result = tool switch
            {
                "ingest_source" => tools.IngestSource(
                    Property<string>(args, "exportedSourceRoot"),
                    Property<string>(args, "dbPath"),
                    Property<string>(args, "modifiedSourceRoot")),
                "update_components" => Update(tools, args),
                _ => throw new InvalidOperationException(tool),
            };
            if (result.IsError == true)
                throw new InvalidOperationException(ParseText(result));
            return Task.FromResult(JsonSerializer.Deserialize<T>(
                ParseText(result), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
        }

        private CallToolResult Update(KnowledgeTools tools, object args)
        {
            PartialCalls++;
            return tools.UpdateComponents(
                Property<string>(args, "exportedSourceRoot"),
                Property<string>(args, "modifiedSourceRoot"),
                Property<string>(args, "dbPath"),
                Property<string[]>(args, "relativePaths"));
        }

        private static string ParseText(CallToolResult result) =>
            ((TextContentBlock)result.Content.Single()).Text;
    }
}
