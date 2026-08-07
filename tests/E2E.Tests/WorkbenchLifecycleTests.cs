using System.Text.Json;
using System.Security.Cryptography;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Contracts.Engineering;
using Contracts.Knowledge;
using Mcp.Knowledge.Tools;
using Mcp.VersionControl.Git;
using Mcp.VersionControl.Svn;
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
        Assert.Equal(fixture.Device.Context.SourceRoot, snapshot.SourceRoot);
        Assert.Equal(fixture.OtherDevice.Context.DeviceId, otherSnapshot.DeviceId);
        Assert.Equal(fixture.OtherDevice.Context.WorktreeId, otherSnapshot.WorktreeId);
        Assert.NotEqual(snapshot.SourceRoot, otherSnapshot.SourceRoot);
        Assert.NotEqual(snapshot.KnowledgeDbPath, otherSnapshot.KnowledgeDbPath);
        Assert.Equal(
            databaseHash,
            SHA256.HashData(File.ReadAllBytes(fixture.Device.Context.KnowledgeDbPath)));
        Assert.Empty(fixture.EngineeringCallsAfterRestart);
    }

    [Fact]
    public async Task NewlyCreatedWorkbenchHasCleanSourceStatusWithRuntimeArtifacts()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var coordinator = new WorkbenchCoordinator(
            new EngineeringBoundary(),
            new KnowledgeBoundary(),
            new GitBoundary(),
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));

        var created = await coordinator.CreateWorkbenchAsync(new(
            "Clean status line", Path.Combine(root, "clean-status"), 42, null));

        foreach (var metadata in created.Devices)
        {
            var device = catalog.ResolveDevice(created.Workbench, created.Worktree, metadata);
            File.WriteAllText(device.KnowledgeDbPath, "runtime database");
            Assert.True(File.Exists(Path.Combine(device.WorktreeRoot, "worktree.json")));
            Assert.True(File.Exists(Path.Combine(device.DeviceRoot, "device.json")));
            Assert.True(Directory.Exists(device.SourceRoot));
            Assert.True(Directory.Exists(device.StagingRoot));
            Assert.Empty(RepositoryService.Status(device.WorktreeRoot).Entries);
        }
    }

    [Fact]
    public async Task FeatureMetadataFailureRemovesCheckoutAndNewBranchFromRealRepository()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var git = new GitBoundary();
        var coordinator = new WorkbenchCoordinator(
            new EngineeringBoundary(),
            new KnowledgeBoundary(),
            git,
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        var created = await coordinator.CreateWorkbenchAsync(new(
            "Metadata failure line", Path.Combine(root, "metadata-failure"), 42, null));
        var masterDevicePath = Path.Combine(
            WorkbenchPaths.ResolveWorktree(created.Workbench.RootPath, "master"),
            "devices", "PLC_1", "device.json");
        store.Write(
            masterDevicePath,
            store.Read<DeviceMetadata>(masterDevicePath) with { PlcName = string.Empty });

        await Assert.ThrowsAsync<WorkbenchPathException>(() =>
            coordinator.CreateWorktreeAsync(new(
                catalog.Load(created.Workbench.RootPath), "feature-a", "feature-a")));

        Assert.DoesNotContain(
            RepositoryService.Worktrees(created.Workbench.RepositoryPath).Worktrees,
            item => item.Branch == "feature-a");
        Assert.DoesNotContain(
            RepositoryService.Branches(
                WorkbenchPaths.ResolveWorktree(created.Workbench.RootPath, "master")).Branches,
            branch => branch.Name == "feature-a");
    }

    [Fact]
    public async Task PartialGitCreationRemovesCheckoutAndNewBranchFromRealRepository()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var git = new GitBoundary(failAfterAddWorktree: true);
        var coordinator = new WorkbenchCoordinator(
            new EngineeringBoundary(),
            new KnowledgeBoundary(),
            git,
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        var created = await coordinator.CreateWorkbenchAsync(new(
            "Partial creation line", Path.Combine(root, "partial-creation"), 42, null));

        await Assert.ThrowsAsync<ToolCallException>(() =>
            coordinator.CreateWorktreeAsync(new(
                catalog.Load(created.Workbench.RootPath), "feature-a", "feature-a")));

        Assert.DoesNotContain(
            RepositoryService.Worktrees(created.Workbench.RepositoryPath).Worktrees,
            item => item.Branch == "feature-a");
        Assert.DoesNotContain(
            RepositoryService.Branches(
                WorkbenchPaths.ResolveWorktree(created.Workbench.RootPath, "master")).Branches,
            branch => branch.Name == "feature-a");
    }

    [Fact]
    public async Task UnauthorizedMasterMoveCopiesSelectedXmlToFeatureAndRestoresMaster()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var coordinator = new WorkbenchCoordinator(
            new EngineeringBoundary(), new KnowledgeBoundary(), new GitBoundary(), catalog, store,
            new DeviceReconciler(), new DeviceSourceResolver(_ => { }));
        var created = await coordinator.CreateWorkbenchAsync(new(
            "Recovery line", Path.Combine(root, "recovery"), 42, null));
        var masterRoot = WorkbenchPaths.ResolveWorktree(created.Workbench.RootPath, "master");
        const string sourcePath = "devices/PLC_1/source/Blocks/A.xml";
        var masterSource = WorkbenchPaths.ResolveRelative(masterRoot, sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(masterSource)!);
        File.WriteAllText(masterSource, "<base/>");
        RepositoryService.Add(masterRoot, [sourcePath]);
        RepositoryService.Commit(masterRoot, "base source", null);
        File.WriteAllText(masterSource, "<unauthorized/>");

        var recovery = await coordinator.MoveUnauthorizedMasterChangesAsync(
            created.Workbench.WorkbenchId, [sourcePath], "recovered", CancellationToken.None);

        Assert.Equal("<base/>", File.ReadAllText(masterSource));
        var featureRoot = WorkbenchPaths.ResolveWorktree(
            created.Workbench.RootPath,
            catalog.Load(created.Workbench.RootPath).Worktrees.Single(item => item.WorktreeId == recovery.WorktreeId).RelativePath);
        Assert.Equal("<unauthorized/>", File.ReadAllText(WorkbenchPaths.ResolveRelative(featureRoot, sourcePath)));
        Assert.True(File.Exists(Path.Combine(recovery.RecoveryRoot, "recovery.json")));
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
        // The 1.2 create flow already bootstrapped a baseline export into the source trees.
        var baselineMain = File.ReadAllText(Path.Combine(plc1.SourceRoot, "Blocks", "Main.xml"));
        engineering.SetExport("PLC_1", "v1");
        engineering.SetExport("PLC_2", "v1");

        await coordinator.StageRefreshAsync(plc1, CancellationToken.None);
        var rejectedPreview = coordinator.PreviewRefresh(plc1);
        var rejected = await coordinator.ApplyRefreshAsync(
            plc1, ApprovedReconciliation.Rejected(rejectedPreview), CancellationToken.None);
        Assert.Equal(RefreshApplyState.Rejected, rejected.State);
        Assert.Equal(baselineMain, File.ReadAllText(Path.Combine(plc1.SourceRoot, "Blocks", "Main.xml")));
        Assert.Single(RepositoryService.Log(plc1.WorktreeRoot, 10).Commits);

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
        Assert.Equal(RefreshApplyState.FilesUpdated, plc1Apply.State);
        Assert.Null(plc1Apply.CommitSha);
        Assert.Null(plc1Apply.Error);
        Assert.Empty(git.AddedPathBatches);
        Assert.True(ReadDevice(store, plc1).Knowledge.BaselineStale);
        Assert.False(ReadDevice(store, plc2).Knowledge.BaselineStale);

        await coordinator.StageRefreshAsync(plc2, CancellationToken.None);
        var plc2Preview = coordinator.PreviewRefresh(plc2);
        var plc2Apply = await coordinator.ApplyRefreshAsync(
            plc2, ApproveAllChanges(plc2Preview),
            CancellationToken.None);
        Assert.Equal(RefreshApplyState.FilesUpdated, plc2Apply.State);
        Assert.Null(plc2Apply.CommitSha);
        Assert.Null(plc2Apply.Error);
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

        var baselineFile = Path.Combine(plc1.SourceRoot, "Blocks", "Main.xml");
        var baselineHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(baselineFile)));
        var baselineTimestamp = File.GetLastWriteTimeUtc(baselineFile);
        engineering.SetExport("PLC_1", "stale-preview");
        await coordinator.StageRefreshAsync(plc1, CancellationToken.None);
        var noOp = await coordinator.ApplyRefreshAsync(
            plc1, new(coordinator.PreviewRefresh(plc1), new HashSet<string>()),
            CancellationToken.None);
        Assert.Equal(RefreshApplyState.NoChanges, noOp.State);
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
        Assert.Equal(RefreshApplyState.FilesUpdated, postEditRefresh.State);
        Assert.Null(postEditRefresh.CommitSha);
        Assert.Null(postEditRefresh.Error);
        Assert.True(File.Exists(overlay));
        Assert.True(ReadDevice(store, plc1).Knowledge.BaselineStale);

        var session = SessionManager.CreateNewSession(plc1, new ChatRequestSettings(), "context");
        session.Messages.Add(ChatMessage.User("hello"));
        SessionManager.SaveSession(plc1, session);
        Assert.Equal("hello", SessionManager.LoadSession(plc1, session.Header.SessionId)!.Messages.Single().Content);
        Assert.Contains(".automation/sessions", SessionManager.ResolveSessionPath(plc1, session.Header.SessionId)!.Replace('\\', '/'));
        Assert.DoesNotContain(
            RepositoryService.Status(plc1.WorktreeRoot).Entries,
            entry => entry.FilePath.Contains(".automation", StringComparison.Ordinal));

        RepositoryService.Add(plc1.WorktreeRoot, [
            Path.GetRelativePath(plc1.WorktreeRoot, Path.Combine(plc1.SourceRoot, "Blocks", "Main.xml"))
                .Replace('\\', '/'),
            Path.GetRelativePath(plc1.WorktreeRoot, Path.Combine(plc2.SourceRoot, "Blocks", "Main.xml"))
                .Replace('\\', '/'),
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
    public async Task NewWorkbenchRecordsSvnBaselineAndCommittedRevisionState()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var order = new List<string>();
        var coordinator = new WorkbenchCoordinator(
            new EngineeringBoundary(order),
            new KnowledgeBoundary(),
            new GitBoundary(order: order),
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));

        var created = await coordinator.CreateWorkbenchAsync(new(
            "Native baseline line", Path.Combine(root, "native-baseline"), 42, null));

        // TIA Save As must run into the EMPTY tia/ dir; the SVN checkout (allowObstructions)
        // only adopts the saved project after the TIA session is closed.
        Assert.True(order.IndexOf("engineering:save_project_as") >= 0);
        Assert.True(
            order.IndexOf("engineering:save_project_as") < order.IndexOf("version:svn_checkout"));
        Assert.True(
            order.IndexOf("engineering:disconnect") < order.IndexOf("version:svn_checkout"));
        Assert.True(
            order.IndexOf("version:svn_checkout") < order.IndexOf("version:svn_commit"));

        var workbenchRoot = created.Workbench.RootPath;
        var masterRoot = WorkbenchPaths.ResolveWorktree(workbenchRoot, "master");
        var tiaStore = WorkbenchPaths.ResolveTiaStore(masterRoot);

        Assert.Equal("1.2", created.Workbench.SchemaVersion);
        Assert.Equal(
            Path.Combine(workbenchRoot, "repository.svn"),
            created.Workbench.SvnRepositoryPath);
        Assert.True(Directory.Exists(created.Workbench.SvnRepositoryPath));
        Assert.Equal(@"C:\Fixture\Line.ap17", created.Workbench.OriginProjectPath);
        Assert.NotNull(created.Workbench.OriginImportedAt);
        var managedPath = Assert.IsType<string>(created.Workbench.ManagedTiaProjectPath);
        Assert.Equal(tiaStore, Path.GetDirectoryName(managedPath));
        Assert.True(File.Exists(managedPath));
        Assert.Equal(managedPath, created.Workbench.SourceProjectPath);
        Assert.Equal(managedPath, created.Worktree.ManagedTiaProjectPath);

        // revision.json records the SVN baseline and is part of the clean git baseline commit.
        var revision = EngineeringStateWriter.Read(WorkbenchPaths.ResolveRevisionState(masterRoot));
        Assert.Equal(1, revision.SchemaVersion);
        Assert.Equal("^/native/main", revision.Svn.Url);
        Assert.True(revision.Svn.Revision >= 1);
        Assert.Equal("PLC_1:checksum-PLC_1;PLC_2:checksum-PLC_2", revision.Tia.ProjectChecksum);
        Assert.Null(revision.Safety.FSignature);
        Assert.Equal(EngineeringCompileStatus.Success, revision.Validation.CompileStatus);
        Assert.Empty(RepositoryService.Status(masterRoot).Entries);
        var gitLog = RepositoryService.Log(masterRoot, 10).Commits;
        var baseline = Assert.Single(gitLog);
        Assert.Contains("engineering-state/revision.json", baseline.Files);
        Assert.Contains("devices/PLC_1/source/Blocks/Main.xml", baseline.Files);

        // The SVN native store holds the baseline revision and the working copy is clean.
        var svn = new SvnRepositoryService();
        var mainUrl = new Uri(created.Workbench.SvnRepositoryPath! + Path.DirectorySeparatorChar) + "native/main";
        var svnLog = svn.Log(mainUrl, 10);
        Assert.Contains(
            svnLog.Entries,
            entry => entry.Message.Contains("native: initial managed TIA project baseline", StringComparison.Ordinal));
        Assert.True(svn.Status(tiaStore).IsClean);

        // The Save As result really is versioned: a fresh checkout of native/main contains
        // the complete project tree the fake TIA wrote into the previously plain directory.
        var roundTrip = Path.Combine(root, "baseline-roundtrip");
        svn.Checkout(mainUrl, roundTrip);
        Assert.Equal(
            "managed TIA project placeholder",
            File.ReadAllText(Path.Combine(roundTrip, "Line.ap17")));
        Assert.Equal(
            "project system data",
            File.ReadAllText(Path.Combine(roundTrip, "IM", "project-data.bin")));
        Assert.Equal(
            "tia native system data",
            File.ReadAllText(Path.Combine(roundTrip, "System", "pe.cfg")));
        // ...while the legacy app export cache copied along by Save As was stripped before
        // the baseline — it is app state, not TIA project data.
        Assert.False(Directory.Exists(Path.Combine(tiaStore, "Exports")));
        Assert.False(Directory.Exists(Path.Combine(roundTrip, "Exports")));
    }

    [Fact]
    public async Task CompileFailureStillCompletesImportWithFailedCompileStatus()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var engineering = new EngineeringBoundary();
        engineering.SetCompileState("error");
        var coordinator = new WorkbenchCoordinator(
            engineering,
            new KnowledgeBoundary(),
            new GitBoundary(),
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));

        var created = await coordinator.CreateWorkbenchAsync(new(
            "Compile failure line", Path.Combine(root, "compile-failure"), 42, null));

        var masterRoot = WorkbenchPaths.ResolveWorktree(created.Workbench.RootPath, "master");
        var revision = EngineeringStateWriter.Read(WorkbenchPaths.ResolveRevisionState(masterRoot));
        Assert.Equal(EngineeringCompileStatus.Failed, revision.Validation.CompileStatus);
        Assert.Null(revision.Tia.ProjectChecksum);
        Assert.True(revision.Svn.Revision >= 1);
        Assert.Single(RepositoryService.Log(masterRoot, 10).Commits);
    }

    [Fact]
    public async Task FailedSaveProjectAsRollsBackSvnRepositoryAndWorkbench()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var engineering = new EngineeringBoundary { FailSaveProjectAs = true };
        var coordinator = new WorkbenchCoordinator(
            engineering,
            new KnowledgeBoundary(),
            new GitBoundary(),
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        var workbenchRoot = Path.Combine(root, "save-as-failure");

        await Assert.ThrowsAsync<ToolCallException>(() =>
            coordinator.CreateWorkbenchAsync(new("Save failure line", workbenchRoot, 42, null)));

        Assert.False(File.Exists(Path.Combine(workbenchRoot, "workbench.json")));
        Assert.False(Directory.Exists(Path.Combine(workbenchRoot, "repository.svn")));
        Assert.False(Directory.Exists(Path.Combine(workbenchRoot, "repository.git")));
        Assert.False(Directory.Exists(Path.Combine(workbenchRoot, "worktrees")));
    }

    [Fact]
    public async Task RestoreTiaProjectChecksOutTheRecordedNativeRevision()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var coordinator = new WorkbenchCoordinator(
            new EngineeringBoundary(),
            new KnowledgeBoundary(),
            new GitBoundary(),
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        var created = await coordinator.CreateWorkbenchAsync(new(
            "Restore line", Path.Combine(root, "restore"), 42, null));
        var masterRoot = WorkbenchPaths.ResolveWorktree(created.Workbench.RootPath, "master");
        var tiaStore = WorkbenchPaths.ResolveTiaStore(masterRoot);
        var managedPath = Assert.IsType<string>(created.Workbench.ManagedTiaProjectPath);
        var baselineRevision = EngineeringStateWriter.Read(
            WorkbenchPaths.ResolveRevisionState(masterRoot)).Svn.Revision!.Value;

        // A later native change advances the SVN HEAD beyond the recorded baseline.
        var svn = new SvnRepositoryService();
        File.AppendAllText(managedPath, "later change");
        svn.AddRecursive(tiaStore);
        var later = svn.Commit(tiaStore, "native: later change");
        Assert.True(later.Committed);
        Assert.Equal(baselineRevision + 1, later.Revision);

        var restored = await coordinator.RestoreTiaProjectAsync(
            created.Workbench.WorkbenchId,
            created.Worktree.WorktreeId);

        Assert.Equal(baselineRevision, restored.SvnRevision);
        Assert.Equal("^/native/main", restored.SvnUrl);
        Assert.Equal(
            RepositoryService.Log(masterRoot, 1).Commits.Single().Sha,
            restored.GitCommit);
        // The restore target is deterministic: <workbenchRoot>/export/<checksum>.
        Assert.StartsWith(
            Path.Combine(created.Workbench.RootPath, "export"),
            restored.RestoredDirectory);
        var restoredProject = Assert.IsType<string>(restored.RestoredProjectPath);
        Assert.Equal("managed TIA project placeholder", File.ReadAllText(restoredProject));
        // Lean restore: svn export leaves no .svn metadata behind.
        Assert.False(Directory.Exists(Path.Combine(restored.RestoredDirectory, ".svn")));
        // The live working copy is untouched by the restore.
        Assert.Equal("managed TIA project placeholderlater change", File.ReadAllText(managedPath));
    }

    [Fact]
    public async Task RestoreTiaProjectAtOlderGitCommitReadsHistoricalRevisionState()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var coordinator = new WorkbenchCoordinator(
            new EngineeringBoundary(),
            new KnowledgeBoundary(),
            new GitBoundary(),
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        var created = await coordinator.CreateWorkbenchAsync(new(
            "Historical restore line", Path.Combine(root, "restore-historical"), 42, null));
        var masterRoot = WorkbenchPaths.ResolveWorktree(created.Workbench.RootPath, "master");
        var tiaStore = WorkbenchPaths.ResolveTiaStore(masterRoot);
        var managedPath = Assert.IsType<string>(created.Workbench.ManagedTiaProjectPath);
        var firstSha = RepositoryService.Log(masterRoot, 1).Commits.Single().Sha;
        var firstRevision = EngineeringStateWriter.Read(
            WorkbenchPaths.ResolveRevisionState(masterRoot)).Svn.Revision!.Value;

        // A later savepoint: native change + second SVN revision + updated revision.json + git commit.
        var svn = new SvnRepositoryService();
        File.AppendAllText(managedPath, "second savepoint");
        svn.AddRecursive(tiaStore);
        var secondSvn = svn.Commit(tiaStore, "native: second savepoint");
        EngineeringStateWriter.Write(masterRoot, EngineeringStateWriter.Create(
            "^/native/main",
            secondSvn.Revision,
            "PLC_1:checksum-PLC_1;PLC_2:checksum-PLC_2",
            null,
            EngineeringCompileStatus.Success));
        RepositoryService.CommitSelected(
            masterRoot, ["engineering-state/revision.json"], "savepoint 2", null);

        var restored = await coordinator.RestoreTiaProjectAsync(
            created.Workbench.WorkbenchId,
            created.Worktree.WorktreeId,
            firstSha);

        Assert.Equal(firstSha, restored.GitCommit);
        Assert.Equal(firstRevision, restored.SvnRevision);
        var restoredProject = Assert.IsType<string>(restored.RestoredProjectPath);
        Assert.Equal("managed TIA project placeholder", File.ReadAllText(restoredProject));
        // The restore read window is closed again: the worktree file matches HEAD (savepoint 2).
        var current = EngineeringStateWriter.Read(WorkbenchPaths.ResolveRevisionState(masterRoot));
        Assert.Equal(secondSvn.Revision, current.Svn.Revision);
    }

    [Fact]
    public async Task CombinedCommitAdvancesSvnAndGitToTheSameTiaState()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var coordinator = new WorkbenchCoordinator(
            new EngineeringBoundary(),
            new KnowledgeBoundary(),
            new GitBoundary(),
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        var created = await coordinator.CreateWorkbenchAsync(new(
            "Combined commit line", Path.Combine(root, "combined-commit"), 42, null));
        var masterRoot = WorkbenchPaths.ResolveWorktree(created.Workbench.RootPath, "master");
        var tiaStore = WorkbenchPaths.ResolveTiaStore(masterRoot);
        var managedPath = Assert.IsType<string>(created.Workbench.ManagedTiaProjectPath);
        var baselineSvnRevision = EngineeringStateWriter.Read(
            WorkbenchPaths.ResolveRevisionState(masterRoot)).Svn.Revision!.Value;
        var headBefore = RepositoryService.Log(masterRoot, 1).Commits.Single().Sha;

        // Simulate an authorized TIA change: the managed project changed natively and the
        // accepted source XML is written into master with the master-gate pending records.
        File.AppendAllText(managedPath, "tia-side change");
        const string sourcePath = "devices/PLC_1/source/Blocks/Main.xml";
        var sourceFile = WorkbenchPaths.ResolveRelative(masterRoot, sourcePath);
        File.AppendAllText(sourceFile, "<!-- accepted from TIA -->");
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(sourceFile))).ToLowerInvariant();
        new WorkbenchWritePolicy(store).WritePending(masterRoot, new PendingMasterSynchronization(
            WorkbenchWritePolicy.PendingSchemaVersion,
            created.Worktree.WorktreeId,
            new[] { new PendingMasterSource(sourcePath, "comparison-1", headBefore, fingerprint, fingerprint) }));

        var commit = await coordinator.CommitSourceAsync(
            created.Workbench.WorkbenchId,
            created.Worktree.WorktreeId,
            [sourcePath],
            "accept Main change",
            CancellationToken.None);

        // Ordinary commits are git-only: SVN and revision.json have not moved yet.
        var revisionBeforeSavepoint = EngineeringStateWriter.Read(WorkbenchPaths.ResolveRevisionState(masterRoot));
        Assert.Equal(baselineSvnRevision, revisionBeforeSavepoint.Svn.Revision);
        var svn = new SvnRepositoryService();
        var mainUrl = new Uri(created.Workbench.SvnRepositoryPath! + Path.DirectorySeparatorChar) + "native/main";
        Assert.Equal(baselineSvnRevision, svn.Log(mainUrl, 1).Entries.Single().Revision);
        var sourceCommit = RepositoryService.Log(masterRoot, 1).Commits.Single();
        Assert.Equal(commit.Sha, sourceCommit.Sha);
        Assert.Contains(sourcePath, sourceCommit.Files);

        // The explicit savepoint binds the TIA state: SVN revision + revision.json + git commit.
        var savepoint = await coordinator.CreateNativeSavepointAsync(
            created.Workbench.WorkbenchId,
            created.Worktree.WorktreeId,
            "accept Main change",
            CancellationToken.None);

        var revision = EngineeringStateWriter.Read(WorkbenchPaths.ResolveRevisionState(masterRoot));
        Assert.Equal(baselineSvnRevision + 1, revision.Svn.Revision);
        var headEntry = svn.Log(mainUrl, 1).Entries.Single();
        Assert.Equal(revision.Svn.Revision, headEntry.Revision);
        Assert.StartsWith("accept Main change [", headEntry.Message);
        Assert.Contains("native", headEntry.Message);
        Assert.Equal(EngineeringCompileStatus.Success, revision.Validation.CompileStatus);

        var head = RepositoryService.Log(masterRoot, 1).Commits.Single();
        Assert.Equal(savepoint.Sha, head.Sha);
        Assert.Contains("engineering-state/revision.json", head.Files);
        Assert.Empty(RepositoryService.Status(masterRoot).Entries);
        Assert.False(File.Exists(PendingCommitStore.PathFor(masterRoot)));
    }

    [Fact]
    public async Task SafetyOnlyChangeStillCreatesGitCommitWithUnchangedXml()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var coordinator = new WorkbenchCoordinator(
            new EngineeringBoundary(),
            new KnowledgeBoundary(),
            new GitBoundary(),
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        var created = await coordinator.CreateWorkbenchAsync(new(
            "Safety line", Path.Combine(root, "safety-only"), 42, null));
        var masterRoot = WorkbenchPaths.ResolveWorktree(created.Workbench.RootPath, "master");
        var baseline = EngineeringStateWriter.Read(WorkbenchPaths.ResolveRevisionState(masterRoot));
        var sourceFile = WorkbenchPaths.ResolveRelative(
            masterRoot, "devices/PLC_1/source/Blocks/Main.xml");
        var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFile)));

        // Record a prior F-signature, so the current (still uncapturable) null signature
        // classifies as a safety change — the V1 stand-in for a real safety-only savepoint.
        EngineeringStateWriter.Write(masterRoot, baseline with
        {
            Safety = new EngineeringSafetyState("F-SIG-1"),
        });
        RepositoryService.CommitSelected(
            masterRoot, ["engineering-state/revision.json"], "record F-signature", null);

        var commit = await coordinator.CreateNativeSavepointAsync(
            created.Workbench.WorkbenchId,
            created.Worktree.WorktreeId,
            "safety update",
            CancellationToken.None);

        var head = RepositoryService.Log(masterRoot, 1).Commits.Single();
        Assert.Equal(commit.Sha, head.Sha);
        Assert.Equal(["engineering-state/revision.json"], head.Files);
        // The XML stayed byte-identical; only revision.json moved.
        Assert.Equal(
            sourceHash,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFile))));
        var revision = EngineeringStateWriter.Read(WorkbenchPaths.ResolveRevisionState(masterRoot));
        Assert.Null(revision.Safety.FSignature);
        // No native change: the SVN store was not advanced beyond the baseline revision.
        var mainUrl = new Uri(created.Workbench.SvnRepositoryPath! + Path.DirectorySeparatorChar) + "native/main";
        var headRevision = new SvnRepositoryService().Log(mainUrl, 1).Entries.Single().Revision;
        Assert.Equal(baseline.Svn.Revision, headRevision);
        Assert.Equal(baseline.Svn.Revision, revision.Svn.Revision);
    }

    [Fact]
    public async Task FeatureWorktreeEvolvesOnItsOwnSvnBranchIndependentlyFromMaster()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var coordinator = new WorkbenchCoordinator(
            new EngineeringBoundary(),
            new KnowledgeBoundary(),
            new GitBoundary(),
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        var created = await coordinator.CreateWorkbenchAsync(new(
            "Feature line", Path.Combine(root, "feature-svn"), 42, null));
        var masterRoot = WorkbenchPaths.ResolveWorktree(created.Workbench.RootPath, "master");
        var masterManagedPath = Assert.IsType<string>(created.Workbench.ManagedTiaProjectPath);
        var masterBaseRevision = EngineeringStateWriter.Read(
            WorkbenchPaths.ResolveRevisionState(masterRoot)).Svn.Revision!.Value;
        var masterHead = RepositoryService.Log(masterRoot, 1).Commits.Single().Sha;

        var feature = await coordinator.CreateWorktreeAsync(new(
            catalog.Load(created.Workbench.RootPath), "feature-a", "feature-a"));

        Assert.Equal("^/native/branches/feature-a", feature.SvnUrl);
        Assert.Equal(masterBaseRevision, feature.BaseSvnRevision);
        Assert.Equal(masterHead, feature.BaseCommit);
        var featureRoot = WorkbenchPaths.ResolveWorktree(created.Workbench.RootPath, "feature-a");
        var featureTia = WorkbenchPaths.ResolveTiaStore(featureRoot);
        Assert.Equal(Path.Combine(featureTia, "Line.ap17"), feature.ManagedTiaProjectPath);
        Assert.True(File.Exists(feature.ManagedTiaProjectPath));

        var svn = new SvnRepositoryService();
        var repoUri = new Uri(created.Workbench.SvnRepositoryPath! + Path.DirectorySeparatorChar).ToString();
        var mainUrl = repoUri + "native/main";
        var branchUrl = repoUri + "native/branches/feature-a";
        var branchHeadAfterCopy = svn.Log(branchUrl, 1).Entries.Single().Revision;
        Assert.True(branchHeadAfterCopy > masterBaseRevision);

        // Master: the source commit is git-only; the explicit savepoint advances ^/native/main only.
        const string sourcePath = "devices/PLC_1/source/Blocks/Main.xml";
        File.AppendAllText(masterManagedPath, "master native change");
        var masterSource = WorkbenchPaths.ResolveRelative(masterRoot, sourcePath);
        File.AppendAllText(masterSource, "<!-- master change -->");
        var masterFingerprint = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(masterSource))).ToLowerInvariant();
        new WorkbenchWritePolicy(store).WritePending(masterRoot, new PendingMasterSynchronization(
            WorkbenchWritePolicy.PendingSchemaVersion,
            created.Worktree.WorktreeId,
            new[]
            {
                new PendingMasterSource(sourcePath, "comparison-1", masterHead, masterFingerprint, masterFingerprint),
            }));
        await coordinator.CommitSourceAsync(
            created.Workbench.WorkbenchId,
            created.Worktree.WorktreeId,
            [sourcePath],
            "master change",
            CancellationToken.None);
        // Git-only: the SVN store has not moved yet (main stays at the baseline revision).
        Assert.Equal(masterBaseRevision, svn.Log(mainUrl, 1).Entries.Single().Revision);

        await coordinator.CreateNativeSavepointAsync(
            created.Workbench.WorkbenchId,
            created.Worktree.WorktreeId,
            "master change",
            CancellationToken.None);
        var mainHeadAfterMasterCommit = svn.Log(mainUrl, 1).Entries.Single().Revision;
        Assert.Equal(branchHeadAfterCopy + 1, mainHeadAfterMasterCommit);
        Assert.Equal(branchHeadAfterCopy, svn.Log(branchUrl, 1).Entries.Single().Revision);
        Assert.DoesNotContain(
            svn.Log(branchUrl, 20).Entries,
            entry => entry.Message.Contains("master change", StringComparison.Ordinal));

        // Feature: same split — git-only source commit, savepoint advances its own branch only.
        File.AppendAllText(feature.ManagedTiaProjectPath!, "feature native change");
        var featureSource = WorkbenchPaths.ResolveRelative(featureRoot, sourcePath);
        File.AppendAllText(featureSource, "<!-- feature change -->");
        await coordinator.CommitSourceAsync(
            created.Workbench.WorkbenchId,
            feature.WorktreeId,
            [sourcePath],
            "feature change",
            CancellationToken.None);
        // Git-only: the feature's SVN branch has not moved since its copy.
        Assert.Equal(branchHeadAfterCopy, svn.Log(branchUrl, 1).Entries.Single().Revision);
        await coordinator.CreateNativeSavepointAsync(
            created.Workbench.WorkbenchId,
            feature.WorktreeId,
            "feature change",
            CancellationToken.None);
        var branchHead = svn.Log(branchUrl, 1).Entries.Single();
        Assert.Equal(mainHeadAfterMasterCommit + 1, branchHead.Revision);
        Assert.StartsWith("feature change [", branchHead.Message);
        Assert.Equal(mainHeadAfterMasterCommit, svn.Log(mainUrl, 1).Entries.Single().Revision);
        Assert.DoesNotContain(
            svn.Log(mainUrl, 20).Entries,
            entry => entry.Message.Contains("feature change", StringComparison.Ordinal));
        var featureRevision = EngineeringStateWriter.Read(
            WorkbenchPaths.ResolveRevisionState(featureRoot));
        Assert.Equal("^/native/branches/feature-a", featureRevision.Svn.Url);
        Assert.Equal(branchHead.Revision, featureRevision.Svn.Revision);

        // Restore from the feature's revision.json pins the feature branch revision.
        var restored = await coordinator.RestoreTiaProjectAsync(
            created.Workbench.WorkbenchId,
            feature.WorktreeId);
        Assert.Equal("^/native/branches/feature-a", restored.SvnUrl);
        Assert.Equal(branchHead.Revision, restored.SvnRevision);
        Assert.Contains(
            "feature native change",
            File.ReadAllText(Assert.IsType<string>(restored.RestoredProjectPath)));
        Assert.DoesNotContain(
            "master native change",
            File.ReadAllText(restored.RestoredProjectPath!));
    }

    [Fact]
    public async Task RemovedFeatureWorktreeDeletesTiaCopyButKeepsSvnBranch()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "catalog"));
        var coordinator = new WorkbenchCoordinator(
            new EngineeringBoundary(),
            new KnowledgeBoundary(),
            new GitBoundary(),
            catalog,
            store,
            new DeviceReconciler(),
            new DeviceSourceResolver(_ => { }));
        var created = await coordinator.CreateWorkbenchAsync(new(
            "Removal line", Path.Combine(root, "feature-removal"), 42, null));
        var feature = await coordinator.CreateWorktreeAsync(new(
            catalog.Load(created.Workbench.RootPath), "feature-a", "feature-a"));
        var featureRoot = WorkbenchPaths.ResolveWorktree(created.Workbench.RootPath, "feature-a");
        Assert.True(Directory.Exists(WorkbenchPaths.ResolveTiaStore(featureRoot)));

        await coordinator.DeleteWorktreeAsync(
            catalog.Load(created.Workbench.RootPath), feature.WorktreeId, CancellationToken.None);

        Assert.False(Directory.Exists(featureRoot));
        var repoUri = new Uri(created.Workbench.SvnRepositoryPath! + Path.DirectorySeparatorChar).ToString();
        Assert.NotEmpty(new SvnRepositoryService().Log(repoUri + "native/branches/feature-a", 5).Entries);
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
            Assert.True(File.Exists(Path.Combine(root, "source", "Blocks", "Main.xml")));
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
        private static readonly SvnRepositoryService Svn = new();
        private readonly bool failAfterAddWorktree;
        private readonly List<string>? order;

        public GitBoundary(bool failAfterAddWorktree = false, List<string>? order = null)
        {
            this.failAfterAddWorktree = failAfterAddWorktree;
            this.order = order;
        }

        public List<string[]> AddedPathBatches { get; } = [];

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            order?.Add($"version:{tool}");
            object result = tool switch
            {
                "vc_init_shared" => RepositoryService.InitShared(
                    Property<string>(args, "workbenchRoot"), Property<string>(args, "masterWorktreePath")),
                "vc_add_worktree" => AddWorktree(args),
                "vc_remove_worktree" => RemoveWorktree(args),
                "vc_add" => Add(args),
                "vc_commit" => Commit(args),
                "vc_commit_selected" => CommitSelected(args),
                "vc_log" => Log(args),
                "vc_restore" => RepositoryService.Restore(
                    Property<string>(args, "repoPath"),
                    args.GetType().GetProperty("filePath")?.GetValue(args) as string,
                    args.GetType().GetProperty("sourceSha")?.GetValue(args) as string),
                "vc_merge" => RepositoryService.Merge(
                    Property<string>(args, "targetWorktreePath"), Property<string>(args, "sourceBranch")),
                "svn_init_shared" => SvnInitShared(args),
                "svn_checkout" => Svn.Checkout(
                    Property<string>(args, "url"),
                    Property<string>(args, "path"),
                    args.GetType().GetProperty("allowObstructions")?.GetValue(args) is true),
                "svn_commit" => SvnCommit(args),
                "svn_status" => SvnStatus(args),
                "svn_copy_branch" => Svn.CopyBranch(
                    $"{Property<string>(args, "repoUrl").TrimEnd('/')}/native/{Property<string>(args, "sourceBranch")}",
                    Property<long>(args, "revision"),
                    Property<string>(args, "newBranch"),
                    Property<string>(args, "message")),
                "svn_log" => Svn.Log(
                    Property<string>(args, "path"),
                    Property<int?>(args, "limit") ?? 20),
                "svn_update" => Svn.UpdateToRevision(
                    Property<string>(args, "path"), Property<long>(args, "revision")),
                "svn_export" => Svn.Export(
                    Property<string>(args, "url"),
                    Property<long>(args, "revision"),
                    Property<string>(args, "path")),
                "vc_show_file" => new ShowFileResult
                {
                    Content = RepositoryService.ShowFile(
                        Property<string>(args, "repoPath"),
                        args.GetType().GetProperty("commitSha")?.GetValue(args) as string,
                        Property<string>(args, "filePath")),
                },
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

        private object AddWorktree(object args)
        {
            var result = RepositoryService.AddWorktree(
                Property<string>(args, "repositoryPath"),
                Property<string>(args, "worktreePath"),
                Property<string>(args, "branchName"),
                Property<string?>(args, "startPoint"));
            if (failAfterAddWorktree)
            {
                throw new ToolCallException(
                    "GIT_PARTIAL",
                    "simulated transport failure after Git created the worktree",
                    null);
            }

            return result;
        }

        private static object RemoveWorktree(object args)
        {
            var deleteValue = args.GetType().GetProperty("deleteBranch")?.GetValue(args);
            return RepositoryService.RemoveWorktree(
                Property<string>(args, "repositoryPath"),
                Property<string>(args, "worktreePath"),
                args.GetType().GetProperty("branchName")?.GetValue(args) as string,
                deleteValue is bool deleteBranch && deleteBranch);
        }

        private static object Commit(object args)
        {
            var commit = RepositoryService.Commit(
                Property<string>(args, "repoPath"), Property<string>(args, "message"), null);
            return new { Sha = commit.Sha };
        }

        private static object CommitSelected(object args)
        {
            var commit = RepositoryService.CommitSelected(
                Property<string>(args, "repoPath"),
                Property<string[]>(args, "paths"),
                Property<string>(args, "message"),
                args.GetType().GetProperty("author")?.GetValue(args) as string);
            return new WorkbenchCommitResult(commit.Sha, commit.Message, commit.Files);
        }

        private static object Log(object args)
        {
            var log = RepositoryService.Log(
                Property<string>(args, "repoPath"),
                Property<int?>(args, "maxCount"),
                args.GetType().GetProperty("filePath")?.GetValue(args) as string);
            return new ConsistencyLogResult
            {
                Commits = log.Commits
                    .Select(commit => new ConsistencyCommit { Sha = commit.Sha })
                    .ToArray(),
            };
        }

        private static object SvnInitShared(object args)
        {
            var result = Svn.CreateShared(Property<string>(args, "workbenchRoot"));
            return new CoordinatorSvnInitResult
            {
                RepositoryPath = result.RepositoryPath,
                RepositoryUri = result.RepositoryUri,
            };
        }

        private static object SvnCommit(object args)
        {
            var path = Property<string>(args, "path");
            Svn.AddRecursive(path);
            var result = Svn.Commit(path, Property<string>(args, "message"));
            return new CoordinatorSvnCommitResult
            {
                Committed = result.Committed,
                Revision = result.Revision,
            };
        }

        private static object SvnStatus(object args) =>
            new CoordinatorSvnStatusResult
            {
                IsClean = Svn.Status(Property<string>(args, "path")).IsClean,
            };
    }

    private sealed class EngineeringBoundary : IMcpToolCaller
    {
        private readonly Dictionary<string, string> versions = new(StringComparer.Ordinal);
        private readonly List<string>? order;
        private bool disconnected;
        private string currentProjectPath = @"C:\Fixture\Line.ap17";
        private string compileState = "success";
        public List<string> ImportCalls { get; } = [];
        public List<string> Calls { get; } = [];

        public EngineeringBoundary(List<string>? order = null) => this.order = order;

        public void SetExport(string plcName, string version) => versions[plcName] = version;
        public void Disconnect() => disconnected = true;

        /// <summary>Simulates a failing compile of the managed project copy during import.</summary>
        public void SetCompileState(string state) => compileState = state;

        /// <summary>Simulates TIA Save As failing during the import bootstrap.</summary>
        public bool FailSaveProjectAs { get; set; }

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            order?.Add($"engineering:{tool}");
            if (disconnected)
                throw new InvalidOperationException("Engineering is disconnected.");
            object result = tool switch
            {
                "connect" => Connect(args),
                "disconnect" => new object(),
                "save_project" => new object(),
                "list_sessions" => Array.Empty<SessionInfo>(),
                "get_project_info" => new ProjectInfo
                {
                    Name = "Line",
                    Path = currentProjectPath,
                    PlcDevices = ["PLC_1", "PLC_2"],
                },
                "save_project_as" => SaveProjectAs(args),
                "compile_plc" => new CompileResult { State = compileState },
                "get_plc_checksums" => Checksums(),
                "rebuild_export" => Export(args),
                "import_block" => Import(args),
                "compile_block" => Compile(args),
                _ => throw new InvalidOperationException(tool),
            };
            return Task.FromResult((T)result);
        }

        private object Connect(object args)
        {
            // A path-based connect switches the fake's active project (e.g. a feature
            // worktree's own managed copy); a session attach keeps the current one.
            var projectPath = args.GetType().GetProperty("projectPath")?.GetValue(args) as string;
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                currentProjectPath = projectPath;
            }

            return new object();
        }

        private object SaveProjectAs(object args)
        {
            if (FailSaveProjectAs)
                throw new ToolCallException("TIA_SAVE_AS_FAILED", "simulated TIA Save As failure", null);
            var target = Property<string>(args, "targetDirectory");
            // A real TIA Save As produces a non-empty project tree (project file + system
            // folders); reproducing that here exercises the adopt-under-SVN-control path.
            Directory.CreateDirectory(target);
            currentProjectPath = Path.Combine(target, "Line.ap17");
            File.WriteAllText(currentProjectPath, "managed TIA project placeholder");
            Directory.CreateDirectory(Path.Combine(target, "IM"));
            File.WriteAllText(Path.Combine(target, "IM", "project-data.bin"), "project system data");
            Directory.CreateDirectory(Path.Combine(target, "System"));
            File.WriteAllText(Path.Combine(target, "System", "pe.cfg"), "tia native system data");
            // Legacy app export cache from older app versions next to the origin project:
            // TIA Save As copies it along; the bootstrap must strip it before the SVN baseline.
            var legacy = Path.Combine(target, "Exports");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(
                Path.Combine(legacy, "metadata.json"),
                """{"schemaVersion":"1.0","exportRoot":"legacy","components":[]}""");
            File.WriteAllText(Path.Combine(legacy, "plc-knowledge.db"), "stale knowledge cache");
            return new CoordinatorSaveProjectAsResult { ManagedProjectPath = currentProjectPath };
        }

        private PlcChecksumInfo[] Checksums() =>
            new[] { "PLC_1", "PLC_2" }
                .Select(plc => new PlcChecksumInfo
                {
                    PlcName = plc,
                    SoftwareChecksum = $"checksum-{plc}",
                })
                .ToArray();

        private SyncResult[] Export(object args)
        {
            var output = Property<string>(args, "outputDir");
            var plc = Property<string>(args, "plcName");
            Directory.CreateDirectory(Path.Combine(output, "Blocks"));
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "Main [OB1].xml"),
                Path.Combine(output, "Blocks", "Main.xml"), true);
            var version = versions.TryGetValue(plc, out var configured) ? configured : "baseline";
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
                    dbPath: Property<string>(args, "dbPath"),
                    sourceRoot: Property<string>(args, "sourceRoot")),
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
                dbPath: Property<string>(args, "dbPath"),
                relativePaths: Property<string[]>(args, "relativePaths"),
                sourceRoot: Property<string>(args, "sourceRoot"));
        }

        private static string ParseText(CallToolResult result) =>
            ((TextContentBlock)result.Content.Single()).Text;
    }
}
