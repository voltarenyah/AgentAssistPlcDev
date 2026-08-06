using Agent.Mcp;
using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

/// <summary>Phase 4: feature worktrees get their own SVN branch + tia/ working copy.</summary>
public sealed class FeatureSvnBranchTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"feature-svn-branch-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task FeatureCreationBranchesSvnFromMasterBaseRevision()
    {
        var fixture = SvnManagedFixture.Create(root);
        var versionControl = fixture.ScriptVersionControl(new FakeToolCaller());
        var coordinator = fixture.CreateCoordinator(versionControl);

        var feature = await coordinator.CreateWorktreeAsync(
            new CreateWorktreeRequest(fixture.Workbench, "feature-a", "feature-a"),
            CancellationToken.None);

        var copyArgs = versionControl.CallArgs["svn_copy_branch"].Single();
        Assert.Equal("main", Property<string>(copyArgs, "sourceBranch"));
        Assert.Equal(7L, Property<long>(copyArgs, "revision"));
        Assert.Equal("feature-a", Property<string>(copyArgs, "newBranch"));
        Assert.Contains("repository.svn", Property<string>(copyArgs, "repoUrl"), StringComparison.OrdinalIgnoreCase);
        var checkoutArgs = versionControl.CallArgs["svn_checkout"].Single();
        Assert.Contains("native/branches/feature-a", Property<string>(checkoutArgs, "url"));
        Assert.Equal(
            WorkbenchPaths.ResolveTiaStore(fixture.FeatureRoot("feature-a")),
            Property<string>(checkoutArgs, "path"));

        Assert.Equal("^/native/branches/feature-a", feature.SvnUrl);
        Assert.Equal(7, feature.BaseSvnRevision);
        Assert.Equal("head-base", feature.BaseCommit);
        Assert.Equal(
            Path.Combine(WorkbenchPaths.ResolveTiaStore(fixture.FeatureRoot("feature-a")), "Line.ap17"),
            feature.ManagedTiaProjectPath);
        Assert.Equal(
            new[] { "vc_log", "svn_log", "vc_add_worktree", "svn_copy_branch", "svn_checkout" },
            versionControl.Calls);
    }

    [Fact]
    public async Task FeatureCreationSanitizesBranchNameForTheSvnPathSegment()
    {
        var fixture = SvnManagedFixture.Create(root);
        var versionControl = fixture.ScriptVersionControl(new FakeToolCaller());
        var coordinator = fixture.CreateCoordinator(versionControl);

        var feature = await coordinator.CreateWorktreeAsync(
            new CreateWorktreeRequest(fixture.Workbench, "feature x", "feature/x"),
            CancellationToken.None);

        Assert.Equal("feature_x", Property<string>(
            versionControl.CallArgs["svn_copy_branch"].Single(), "newBranch"));
        Assert.Equal("^/native/branches/feature_x", feature.SvnUrl);
        Assert.Equal("feature/x", feature.Branch);
    }

    [Fact]
    public async Task FeatureCreationRejectsExistingSvnBranchBeforeCreatingAnything()
    {
        var fixture = SvnManagedFixture.Create(root);
        var versionControl = fixture.ScriptVersionControl(new FakeToolCaller())
            .Respond("svn_log", new object()); // branch URL resolves → collision
        var coordinator = fixture.CreateCoordinator(versionControl);

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
            coordinator.CreateWorktreeAsync(
                new CreateWorktreeRequest(fixture.Workbench, "feature-a", "feature-a"),
                CancellationToken.None));

        Assert.Equal("SVN_BRANCH_EXISTS", error.Code);
        Assert.Equal(new[] { "vc_log", "svn_log" }, versionControl.Calls);
        Assert.DoesNotContain("vc_add_worktree", versionControl.Calls);
        Assert.False(Directory.Exists(fixture.FeatureRoot("feature-a")));
    }

    [Fact]
    public async Task SvnBranchFailureRollsBackTheGitWorktree()
    {
        var fixture = SvnManagedFixture.Create(root);
        var versionControl = fixture.ScriptVersionControl(new FakeToolCaller(), failCopy: true);
        var coordinator = fixture.CreateCoordinator(versionControl);

        await Assert.ThrowsAsync<ToolCallException>(() =>
            coordinator.CreateWorktreeAsync(
                new CreateWorktreeRequest(fixture.Workbench, "feature-a", "feature-a"),
                CancellationToken.None));

        Assert.Equal(
            new[] { "vc_log", "svn_log", "vc_add_worktree", "svn_copy_branch", "vc_remove_worktree" },
            versionControl.Calls);
        var rollback = versionControl.CallArgs["vc_remove_worktree"].Single();
        Assert.Equal("feature-a", Property<string>(rollback, "branchName"));
        Assert.True(Property<bool>(rollback, "deleteBranch"));
        Assert.Empty(fixture.LoadWorkbench().Worktrees.Where(item => item.Branch == "feature-a"));
    }

    [Fact]
    public async Task SvnCheckoutFailureRemovesThePartialTiaCopyDuringRollback()
    {
        var fixture = SvnManagedFixture.Create(root);
        var versionControl = fixture.ScriptVersionControl(new FakeToolCaller(), failCheckout: true);
        var coordinator = fixture.CreateCoordinator(versionControl);

        await Assert.ThrowsAsync<ToolCallException>(() =>
            coordinator.CreateWorktreeAsync(
                new CreateWorktreeRequest(fixture.Workbench, "feature-a", "feature-a"),
                CancellationToken.None));

        Assert.Contains("vc_remove_worktree", versionControl.Calls);
        Assert.False(Directory.Exists(
            WorkbenchPaths.ResolveTiaStore(fixture.FeatureRoot("feature-a"))));
    }

    [Fact]
    public async Task FeatureCreationWithoutSvnStoreStaysGitOnly()
    {
        var fixture = SvnManagedFixture.Create(root, svnManaged: false);
        var versionControl = new FakeToolCaller()
            .Respond("vc_add_worktree", new object());
        var coordinator = fixture.CreateCoordinator(versionControl);

        var feature = await coordinator.CreateWorktreeAsync(
            new CreateWorktreeRequest(fixture.Workbench, "feature-a", "feature-a", "sha-start"),
            CancellationToken.None);

        Assert.Equal(new[] { "vc_add_worktree" }, versionControl.Calls);
        Assert.Null(feature.SvnUrl);
        Assert.Null(feature.BaseSvnRevision);
        Assert.Equal("sha-start", feature.BaseCommit);
        Assert.Null(feature.ManagedTiaProjectPath);
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

    private sealed class SvnManagedFixture
    {
        private readonly AtomicJsonStore store = new();

        private SvnManagedFixture(string root, WorkbenchMetadata workbench)
        {
            Root = root;
            Workbench = workbench;
        }

        public string Root { get; }
        public WorkbenchMetadata Workbench { get; }

        public static SvnManagedFixture Create(string parent, bool svnManaged = true)
        {
            var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            var store = new AtomicJsonStore();
            var masterRoot = Path.Combine(root, "worktrees", "master");
            Directory.CreateDirectory(masterRoot);
            var managedPath = Path.Combine(masterRoot, "tia", "Line.ap17");
            var workbench = new WorkbenchMetadata(
                svnManaged ? "1.2" : "1.1",
                "wb-1",
                "wb",
                "now",
                root,
                Path.Combine(root, "repository.git"),
                "project-1",
                svnManaged ? managedPath : @"C:\Projects\Line.ap17",
                new[] { new WorkbenchWorktreeRegistration("master-1", "master", "master", "master") },
                SvnRepositoryPath: svnManaged ? Path.Combine(root, "repository.svn") : null,
                ManagedTiaProjectPath: svnManaged ? managedPath : null);
            store.Write(Path.Combine(root, "workbench.json"), workbench);
            store.Write(
                Path.Combine(masterRoot, "worktree.json"),
                new WorktreeMetadata(
                    workbench.SchemaVersion, "master-1", "wb-1", "master", "master", "now", "head-base",
                    "project-1", workbench.SourceProjectPath, new[] { "device-1" }, null,
                    ManagedTiaProjectPath: workbench.ManagedTiaProjectPath));
            var context = WorkbenchPaths.ResolveDevice("wb-1", root, "master-1", "master", "device-1", "PLC_1");
            Directory.CreateDirectory(context.SourceRoot);
            Directory.CreateDirectory(context.StagingRoot);
            store.Write(Path.Combine(context.DeviceRoot, "device.json"), new DeviceMetadata(
                workbench.SchemaVersion, "device-1", "master-1", "PLC_1", "project-1", null, null, null,
                new KnowledgeState(false, new Dictionary<string, string>(), null),
                Array.Empty<DeviceImportRecord>()));

            if (svnManaged)
            {
                Directory.CreateDirectory(Path.Combine(root, "repository.svn"));
                EngineeringStateWriter.Write(masterRoot, EngineeringStateWriter.Create(
                    "^/native/main", 7, "PLC_1:checksum", null, EngineeringCompileStatus.Success));
            }

            return new SvnManagedFixture(root, workbench);
        }

        public string FeatureRoot(string name) =>
            Path.Combine(Root, "worktrees", name);

        public WorkbenchMetadata LoadWorkbench() => store.Read<WorkbenchMetadata>(
            Path.Combine(Root, "workbench.json"));

        /// <summary>Scripts the version-control side of SVN-managed feature creation up to the
        /// branch copy; the checkout writes the managed project file into the new tia/ copy.</summary>
        public FakeToolCaller ScriptVersionControl(
            FakeToolCaller caller,
            bool failCopy = false,
            bool failCheckout = false)
        {
            caller
                .Respond("vc_log", new ConsistencyLogResult
                {
                    Commits = new[] { new ConsistencyCommit { Sha = "head-base" } },
                })
                // svn_log (collision pre-check) intentionally unscripted: the thrown
                // InvalidOperationException stands in for "branch does not exist".
                .Respond("vc_add_worktree", new object());
            if (failCopy)
            {
                caller.Fail("svn_copy_branch", "SVN_COPY_BRANCH_FAILED", "simulated copy failure");
            }
            else
            {
                caller.Respond("svn_copy_branch", new object());
            }

            if (failCheckout)
            {
                caller.Respond("svn_checkout", args =>
                {
                    // Partial checkout: content exists, then the call fails.
                    var path = Property<string>(args, "path");
                    Directory.CreateDirectory(path);
                    File.WriteAllText(Path.Combine(path, "partial.txt"), "partial");
                    throw new ToolCallException("SVN_CHECKOUT_FAILED", "simulated checkout failure", null);
                });
            }
            else
            {
                caller.Respond("svn_checkout", args =>
                {
                    var path = Property<string>(args, "path");
                    Directory.CreateDirectory(path);
                    File.WriteAllText(Path.Combine(path, "Line.ap17"), "managed project");
                    return new object();
                });
            }

            return caller.Respond("vc_remove_worktree", new object());
        }

        public WorkbenchCoordinator CreateCoordinator(FakeToolCaller versionControl)
        {
            var catalog = new WorkbenchCatalog(store, Path.Combine(Root, "catalog"));
            var coordinator = new WorkbenchCoordinator(
                new FakeToolCaller(),
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
