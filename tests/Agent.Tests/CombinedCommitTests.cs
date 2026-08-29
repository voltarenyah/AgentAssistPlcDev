using Agent.Workbench;
using Contracts.Engineering;
using System.Security.Cryptography;
using Xunit;

namespace Agent.Tests;

/// <summary>The native savepoint transaction: SVN revision + git commit describe the same TIA
/// state. Ordinary commits are git-only; only CreateNativeSavepointAsync runs this path.</summary>
public sealed class CombinedCommitTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"combined-commit-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task NativeSavepointAdvancesSvnAndGitWithClassification()
    {
        var fixture = CombinedFixture.Create(root);
        var order = new List<string>();
        var engineering = fixture.ScriptEngineering(new OrderRecordingCaller(order, "engineering"));
        var versionControl = fixture.ScriptVersionControl(new OrderRecordingCaller(order, "version"));
        var coordinator = fixture.CreateCoordinator(engineering, versionControl);

        var result = await coordinator.CreateNativeSavepointAsync(
            CombinedFixture.WorkbenchId,
            CombinedFixture.WorktreeId,
            "accept Main change",
            CancellationToken.None);

        Assert.Equal("head-2", result.Sha);
        var svnCommitArgs = versionControl.CallArgs["svn_commit"].Single();
        var svnMessage = Property<string>(svnCommitArgs, "message");
        Assert.StartsWith("accept Main change [", svnMessage);
        Assert.Contains("native", svnMessage);
        Assert.DoesNotContain("head-", svnMessage);
        Assert.DoesNotContain("safety", svnMessage);

        var revision = fixture.ReadRevisionState();
        Assert.Equal("^/native/main", revision.Svn.Url);
        Assert.Equal(2, revision.Svn.Revision);
        Assert.Equal("PLC_1:new-checksum", revision.Tia.ProjectChecksum);
        Assert.Equal(EngineeringCompileStatus.Success, revision.Validation.CompileStatus);

        var stateArgs = versionControl.CallArgs["vc_commit_state_create"].Single();
        Assert.Equal("head-2", Property<string>(stateArgs, "commitSha"));
        var stateDevices = Property<object[]>(stateArgs, "devices");
        Assert.Contains(stateDevices, item =>
            Property<string>(item, "plcName") == "PLC_1"
            && Property<string>(item, "projectChecksum") == "new-checksum"
            && Property<string>(item, "contentFingerprint") == "new-fingerprint");

        var commitPaths = Property<string[]>(
            versionControl.CallArgs["vc_commit_selected"].Single(), "paths");
        Assert.Contains(EngineeringStateWriter.RelativePath, commitPaths);
        // The TIA session was quiesced before the native commit (Rule 8).
        Assert.True(
            order.IndexOf("engineering:disconnect") < order.IndexOf("version:svn_commit"));
        Assert.False(File.Exists(PendingCommitStore.PathFor(fixture.MasterRoot)));
        // Savepoints do not consume master pending-sync records (ordinary commits own that).
        Assert.Single(fixture.ReadPendingSync());
    }

    [Fact]
    public async Task NativeSavepointAbortsOnCompileFailureWithoutTouchingEitherStore()
    {
        var fixture = CombinedFixture.Create(root);
        var engineering = fixture.ScriptEngineering(new FakeToolCaller(), compileState: "error");
        var versionControl = fixture.ScriptVersionControl(new FakeToolCaller());
        var coordinator = fixture.CreateCoordinator(engineering, versionControl);

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
            coordinator.CreateNativeSavepointAsync(
                CombinedFixture.WorkbenchId,
                CombinedFixture.WorktreeId,
                "accept Main change",
                CancellationToken.None));

        Assert.Equal("PLC_COMPILE_FAILED", error.Code);
        Assert.DoesNotContain("svn_commit", versionControl.Calls);
        Assert.DoesNotContain("vc_commit_selected", versionControl.Calls);
        Assert.Equal(1, fixture.ReadRevisionState().Svn.Revision);
        Assert.False(File.Exists(PendingCommitStore.PathFor(fixture.MasterRoot)));
        Assert.Single(fixture.ReadPendingSync());
    }

    [Fact]
    public async Task GitFailureRecordsPendingCommitAndRetryReusesTheSameSvnRevision()
    {
        var fixture = CombinedFixture.Create(root);
        var engineering = fixture.ScriptEngineering(new FakeToolCaller());
        var versionControl = fixture.ScriptVersionControl(new FakeToolCaller(), failGitCommit: true);
        var coordinator = fixture.CreateCoordinator(engineering, versionControl);

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
            coordinator.CreateNativeSavepointAsync(
                CombinedFixture.WorkbenchId,
                CombinedFixture.WorktreeId,
                "accept Main change",
                CancellationToken.None));

        Assert.Equal("GIT_COMMIT_PENDING", error.Code);
        Assert.Contains("pending-commit.json", error.Message);
        var pending = PendingCommitStore.Read(fixture.MasterRoot);
        Assert.NotNull(pending);
        Assert.Equal(PendingSvnCommit.PendingGitCommit, pending!.Status);
        Assert.Equal(2, pending.SvnRevision);
        Assert.Equal("^/native/main", pending.SvnUrl);
        Assert.Equal(2, fixture.ReadRevisionState().Svn.Revision);
        // The failed attempt left the master pending-sync records untouched.
        Assert.Single(fixture.ReadPendingSync());

        // Retry: git side only, same SVN revision, no second SVN commit.
        var retryVersionControl = new FakeToolCaller()
            .Respond("vc_log", new ConsistencyLogResult
            {
                Commits = new[] { new ConsistencyCommit { Sha = "head-1" } },
            })
            .Respond("vc_commit_selected", new WorkbenchCommitResult(
                "head-2",
                "accept Main change",
                new[] { CombinedFixture.SourcePath, EngineeringStateWriter.RelativePath }))
            .Respond("vc_commit_state_create", new object())
            .Respond("vc_log", new ConsistencyLogResult
            {
                Commits = new[] { new ConsistencyCommit { Sha = "head-2" } },
            });
        var retryCoordinator = fixture.CreateCoordinator(new FakeToolCaller(), retryVersionControl);

        var retried = await retryCoordinator.CreateNativeSavepointAsync(
            CombinedFixture.WorkbenchId,
            CombinedFixture.WorktreeId,
            "accept Main change",
            CancellationToken.None);

        Assert.Equal("head-2", retried.Sha);
        Assert.DoesNotContain("svn_commit", retryVersionControl.Calls);
        Assert.DoesNotContain("svn_status", retryVersionControl.Calls);
        Assert.Equal(2, fixture.ReadRevisionState().Svn.Revision);
        var retryStateArgs = retryVersionControl.CallArgs["vc_commit_state_create"].Single();
        Assert.Equal("head-2", Property<string>(retryStateArgs, "commitSha"));
        var retryStateDevices = Property<object[]>(retryStateArgs, "devices");
        Assert.Contains(retryStateDevices, item =>
            Property<string>(item, "plcName") == "PLC_1"
            && Property<string>(item, "projectChecksum") == "new-checksum");
        Assert.False(File.Exists(PendingCommitStore.PathFor(fixture.MasterRoot)));
        Assert.Single(fixture.ReadPendingSync());
    }

    [Fact]
    public async Task NativeSavepointWithoutAnyChangeIsRejectedCleanly()
    {
        var fixture = CombinedFixture.Create(root, checksum: "PLC_1:new-checksum");
        var engineering = fixture.ScriptEngineering(new FakeToolCaller());
        var versionControl = fixture.ScriptVersionControl(new FakeToolCaller(), svnDirty: false);
        var coordinator = fixture.CreateCoordinator(engineering, versionControl);

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
            coordinator.CreateNativeSavepointAsync(
                CombinedFixture.WorkbenchId,
                CombinedFixture.WorktreeId,
                "no-op",
                CancellationToken.None));

        Assert.Equal("COMMIT_NOTHING_TO_COMMIT", error.Code);
        Assert.DoesNotContain("svn_commit", versionControl.Calls);
        Assert.DoesNotContain("vc_commit_selected", versionControl.Calls);
    }

    [Fact]
    public async Task MasterRefreshAutoCommitIsGitOnly()
    {
        var fixture = CombinedFixture.Create(root);
        fixture.StageRefreshChange("changed A");
        var versionControl = fixture.ScriptVersionControl(new FakeToolCaller(), extraHeadReads: 1);
        var coordinator = fixture.CreateCoordinator(new FakeToolCaller(), versionControl);
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
            commitMessage: "refresh from TIA");

        // Ordinary commits are git-only: no SVN calls, no revision.json rewrite, no compile.
        Assert.Equal("head-2", result.CommitSha);
        Assert.DoesNotContain("svn_commit", versionControl.Calls);
        Assert.DoesNotContain("svn_status", versionControl.Calls);
        var commitPaths = Property<string[]>(
            versionControl.CallArgs["vc_commit_selected"].Single(), "paths");
        Assert.Contains(CombinedFixture.SourcePath, commitPaths);
        Assert.DoesNotContain(EngineeringStateWriter.RelativePath, commitPaths);
        Assert.Equal(1, fixture.ReadRevisionState().Svn.Revision);
        Assert.Empty(fixture.ReadPendingSync());
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static T Property<T>(object value, string name) =>
        (T)value.GetType().GetProperty(name)!.GetValue(value)!;

    private sealed class OrderRecordingCaller(List<string> order, string prefix) : FakeToolCaller
    {
        public override Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            order.Add($"{prefix}:{tool}");
            return base.CallAsync<T>(tool, args, cancellationToken);
        }
    }

    private sealed class CombinedFixture
    {
        public const string WorkbenchId = "wb-combined";
        public const string WorktreeId = "master-1";
        public const string SourcePath = "devices/PLC_1/source/Blocks/A.xml";

        private readonly AtomicJsonStore store = new();

        private CombinedFixture(string root, string managedProjectPath, DeviceContext context)
        {
            Root = root;
            ManagedProjectPath = managedProjectPath;
            Context = context;
        }

        public string Root { get; }
        public string ManagedProjectPath { get; }
        public DeviceContext Context { get; }
        public string MasterRoot => Path.Combine(Root, "worktrees", "master");

        public static CombinedFixture Create(string parent, string checksum = "PLC_1:old-checksum")
        {
            var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            var store = new AtomicJsonStore();
            var masterRoot = Path.Combine(root, "worktrees", "master");
            var tiaStore = WorkbenchPaths.ResolveTiaStore(masterRoot);
            Directory.CreateDirectory(tiaStore);
            var managedPath = Path.Combine(tiaStore, "Line.ap17");
            File.WriteAllText(managedPath, "managed project");
            Directory.CreateDirectory(Path.Combine(root, "repository.svn"));

            var workbench = new WorkbenchMetadata(
                "1.2", WorkbenchId, "wb", "now", root, Path.Combine(root, "repository.git"),
                "project-1", managedPath,
                new[] { new WorkbenchWorktreeRegistration(WorktreeId, "master", "master", "master") },
                SvnRepositoryPath: Path.Combine(root, "repository.svn"),
                ManagedTiaProjectPath: managedPath);
            store.Write(Path.Combine(root, "workbench.json"), workbench);
            store.Write(
                Path.Combine(masterRoot, "worktree.json"),
                new WorktreeMetadata(
                    "1.2", WorktreeId, WorkbenchId, "master", "master", "now", "head-1",
                    "project-1", managedPath, new[] { "device-1" }, null,
                    ManagedTiaProjectPath: managedPath));

            var context = WorkbenchPaths.ResolveDevice(
                WorkbenchId, root, WorktreeId, "master", "device-1", "PLC_1");
            Directory.CreateDirectory(Path.Combine(context.SourceRoot, "Blocks"));
            File.WriteAllText(Path.Combine(context.SourceRoot, "Blocks", "A.xml"), "new A");
            store.Write(Path.Combine(context.DeviceRoot, "device.json"), new DeviceMetadata(
                "1.2", "device-1", WorktreeId, "PLC_1", "project-1", null, null, null,
                new KnowledgeState(false, new Dictionary<string, string>(), null),
                Array.Empty<DeviceImportRecord>()));

            EngineeringStateWriter.Write(masterRoot, EngineeringStateWriter.Create(
                "^/native/main", 1, checksum, null, EngineeringCompileStatus.Success));

            // Simulate the TIA-compare authorization the master gates require.
            var sourceFile = Path.Combine(masterRoot, "devices", "PLC_1", "source", "Blocks", "A.xml");
            var fingerprint = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFile)))
                .ToLowerInvariant();
            new WorkbenchWritePolicy(store).WritePending(masterRoot, new PendingMasterSynchronization(
                WorkbenchWritePolicy.PendingSchemaVersion,
                WorktreeId,
                new[]
                {
                    new PendingMasterSource(SourcePath, "comparison-1", "head-1", fingerprint, fingerprint),
                }));
            return new CombinedFixture(root, managedPath, context);
        }

        /// <summary>Stages a changed A.xml plus manifests on both sides so the reconciler
        /// produces a refresh preview with one changed entry.</summary>
        public void StageRefreshChange(string stagingContent)
        {
            var sourceFile = Path.Combine(Context.SourceRoot, "Blocks", "A.xml");
            File.WriteAllText(sourceFile, "baseline A");
            Directory.CreateDirectory(Path.Combine(Context.StagingRoot, "Blocks"));
            File.WriteAllText(Path.Combine(Context.StagingRoot, "Blocks", "A.xml"), stagingContent);
            WriteManifest(Context.StagingRoot, "Blocks/A.xml");
            WriteManifest(Context.SourceRoot, "Blocks/A.xml");
        }

        private static void WriteManifest(string parent, params string[] paths)
        {
            var components = paths.Select(path => new
            {
                name = Path.GetFileNameWithoutExtension(path),
                sourcePath = $"Program blocks/{Path.GetFileNameWithoutExtension(path)}",
                category = "FC",
                status = "Exported",
                exportedFile = path.Replace('\\', '/'),
            }).ToArray();
            Directory.CreateDirectory(parent);
            File.WriteAllText(
                Path.Combine(parent, "metadata.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    exportStartedUtc = "2026-07-27T00:00:00Z",
                    exportFinishedUtc = "2026-07-27T00:00:01Z",
                    exportRoot = parent,
                    components,
                }));
        }

        public EngineeringRevisionState ReadRevisionState() =>
            EngineeringStateWriter.Read(WorkbenchPaths.ResolveRevisionState(MasterRoot));

        public IReadOnlyList<PendingMasterSource> ReadPendingSync() =>
            new WorkbenchWritePolicy(store).ReadPending(MasterRoot, WorktreeId).Sources;

        /// <summary>Scripts the engineering side: active project already matches, save,
        /// compile, checksums, disconnect.</summary>
        public FakeToolCaller ScriptEngineering(FakeToolCaller caller, string compileState = "success") =>
            caller
                .Respond("get_project_info", new ProjectInfo
                {
                    Name = "Line",
                    Path = ManagedProjectPath,
                    PlcDevices = ["PLC_1"],
                })
                .Respond("save_project", new object())
                .Respond("compile_plc", new CompileResult { State = compileState })
                .Respond("get_plc_checksums", new[]
                {
                    new PlcChecksumInfo
                    {
                        PlcName = "PLC_1",
                        SoftwareChecksum = "new-checksum",
                        ContentFingerprint = "new-fingerprint",
                    },
                })
                .Respond("disconnect", new object());

        /// <summary>Scripts the version-control side: master-gate vc_log, svn status/commit,
        /// git commit, post-commit vc_log.</summary>
        public FakeToolCaller ScriptVersionControl(
            FakeToolCaller caller,
            bool svnDirty = true,
            bool failGitCommit = false,
            int extraHeadReads = 0)
        {
            caller
                .Respond("vc_log", new ConsistencyLogResult
                {
                    Commits = new[] { new ConsistencyCommit { Sha = "head-1" } },
                });
            for (var i = 0; i < extraHeadReads; i++)
            {
                // Callers like the refresh auto-commit read HEAD before CommitSourceAsync
                // re-reads it for the master gate.
                caller.Respond("vc_log", new ConsistencyLogResult
                {
                    Commits = new[] { new ConsistencyCommit { Sha = "head-1" } },
                });
            }
            caller
                .Respond("svn_status", new CoordinatorSvnStatusResult { IsClean = !svnDirty })
                .Respond("svn_commit", new CoordinatorSvnCommitResult { Committed = true, Revision = 2 });
            if (failGitCommit)
            {
                caller.Fail("vc_commit_selected", "GIT_ERROR", "simulated git transport failure");
            }
            else
            {
                caller.Respond("vc_commit_selected", new WorkbenchCommitResult(
                    "head-2",
                    "accept Main change",
                    new[] { SourcePath, EngineeringStateWriter.RelativePath }));
            }

            return caller
                .Respond("vc_commit_state_create", new object())
                .Respond("vc_log", new ConsistencyLogResult
            {
                Commits = new[] { new ConsistencyCommit { Sha = "head-2" } },
            });
        }

        public WorkbenchCoordinator CreateCoordinator(
            FakeToolCaller engineering,
            FakeToolCaller versionControl)
        {
            var catalog = new WorkbenchCatalog(store, Path.Combine(Root, "catalog"));
            var coordinator = new WorkbenchCoordinator(
                engineering,
                new FakeToolCaller(),
                versionControl,
                catalog,
                store,
                new DeviceReconciler(),
                new DeviceSourceResolver(_ => { }));
            coordinator.RegisterWorkbench(catalog.Load(Root));
            return coordinator;
        }
    }
}
