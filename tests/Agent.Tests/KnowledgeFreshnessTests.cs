using Agent.Mcp;
using Agent.Workbench;
using Contracts.Knowledge;
using System.Security.Cryptography;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// The freshness answer the device chat and the device routes report before they rely on the knowledge
/// database. A live conversation answered "your change is not there" from a database that predated the
/// change, so the state has to be derived, not assumed: the persisted flags miss an edit the app did
/// not make (ADR-0012), and the applied hashes are what catch it.
/// </summary>
public sealed class KnowledgeFreshnessTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"knowledge-freshness-{Guid.NewGuid():N}");

    [Fact]
    public void AMissingDatabaseIsMissingAndNeedsARebuild()
    {
        var fixture = Fixture.Create(root);

        var status = fixture.ReadStatus();

        Assert.Equal(DeviceKnowledgeStatus.MissingState, status.State);
        Assert.True(status.RequiresRebuild);
        Assert.Equal(0, status.PendingComponentCount);
        Assert.False(status.IsCurrent);
    }

    [Fact]
    public void TheAppliedHashesDecideCurrent()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteSource("Blocks/A.xml", "<a />");
        fixture.WriteSource("Blocks/B.xml", "<b />");
        fixture.WriteDatabase();
        fixture.WriteAppliedHashes();

        var status = fixture.ReadStatus();

        Assert.Equal(DeviceKnowledgeStatus.CurrentState, status.State);
        Assert.True(status.IsCurrent);
        Assert.False(status.RequiresRebuild);
        Assert.Equal(0, status.PendingComponentCount);
        Assert.Empty(status.ChangedPaths);
        Assert.Empty(status.AddedPaths);
        Assert.Empty(status.RemovedPaths);
    }

    /// <summary>ADR-0012: an edit outside the app sets no staleness flag, so only the hashes can tell
    /// that the database is behind. This is the case a flag-only check would report as current.</summary>
    [Fact]
    public void AnUnflaggedEditOutsideTheAppIsDetectedByTheHashes()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteSource("Blocks/A.xml", "<old />");
        fixture.WriteDatabase();
        fixture.WriteAppliedHashes();
        fixture.WriteSource("Blocks/A.xml", "<new />");

        var status = fixture.ReadStatus();

        Assert.Equal(DeviceKnowledgeStatus.StaleState, status.State);
        Assert.False(status.FlaggedStale);
        Assert.False(status.BaselineStale);
        Assert.Equal(new[] { "Blocks/A.xml" }, status.ChangedPaths);
        // A changed file is exactly what the partial update repairs, so no rebuild is needed.
        Assert.False(status.RequiresRebuild);
    }

    /// <summary>A source file the database holds no component for cannot be added by the partial
    /// update, which refuses an identity it does not already store, so the answer is a rebuild.</summary>
    [Fact]
    public void AnAddedSourceFileIsReportedAndRequiresARebuild()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteSource("Blocks/A.xml", "<a />");
        fixture.WriteDatabase();
        fixture.WriteAppliedHashes();
        fixture.WriteSource("Blocks/New.xml", "<new />");

        var status = fixture.ReadStatus();

        Assert.Equal(DeviceKnowledgeStatus.StaleState, status.State);
        Assert.Equal(new[] { "Blocks/New.xml" }, status.AddedPaths);
        Assert.Empty(status.ChangedPaths);
        Assert.True(status.RequiresRebuild);
        Assert.Equal(1, status.PendingComponentCount);
    }

    /// <summary>A component whose source file is gone cannot be dropped by the partial update, which
    /// only replaces the components a live file names, so the only way back to current is a rebuild.</summary>
    [Fact]
    public void ARemovedSourceFileRequiresARebuild()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteSource("Blocks/A.xml", "<a />");
        fixture.WriteSource("Blocks/Gone.xml", "<gone />");
        fixture.WriteDatabase();
        fixture.WriteAppliedHashes();
        File.Delete(Path.Combine(fixture.Context.SourceRoot, "Blocks", "Gone.xml"));

        var status = fixture.ReadStatus();

        Assert.Equal(DeviceKnowledgeStatus.StaleState, status.State);
        Assert.Equal(new[] { "Blocks/Gone.xml" }, status.RemovedPaths);
        Assert.True(status.RequiresRebuild);
    }

    [Fact]
    public void AStaleBaselineRequiresARebuild()
    {
        var fixture = Fixture.Create(root, baselineStale: true);
        fixture.WriteSource("Blocks/A.xml", "<a />");
        fixture.WriteDatabase();
        fixture.WriteAppliedHashes();

        var status = fixture.ReadStatus();

        Assert.Equal(DeviceKnowledgeStatus.StaleState, status.State);
        Assert.True(status.BaselineStale);
        Assert.True(status.RequiresRebuild);
    }

    /// <summary>A database this app did not build carries no component provenance, so the partial
    /// update would refuse it; the answer is a rebuild.</summary>
    [Fact]
    public void ADatabaseWithNoAppliedHashesRequiresARebuild()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteSource("Blocks/A.xml", "<a />");
        fixture.WriteDatabase();

        var status = fixture.ReadStatus();

        Assert.Equal(new[] { "Blocks/A.xml" }, status.AddedPaths);
        Assert.True(status.RequiresRebuild);
        Assert.Equal(DeviceKnowledgeStatus.StaleState, status.State);
    }

    /// <summary>The refresh path itself, not just the report: a difference the partial update cannot
    /// repair becomes a full rebuild, so the device can always reach <c>current</c>.</summary>
    [Fact]
    public async Task UpdateRepairsAnAddedComponentWithAFullRebuild()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteSource("Blocks/A.xml", "<a />");
        fixture.WriteDatabase();
        fixture.WriteAppliedHashes();
        fixture.WriteSource("Blocks/New.xml", "<new />");

        await fixture.Coordinator.UpdateKnowledgeAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(new[] { "ingest_source" }, fixture.Knowledge.Calls);
        Assert.Equal(DeviceKnowledgeStatus.CurrentState, fixture.ReadStatus().State);
    }

    [Fact]
    public async Task UpdateDropsTheHashOfAComponentTheSourceNoLongerHas()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteSource("Blocks/A.xml", "<a />");
        fixture.WriteSource("Blocks/Gone.xml", "<gone />");
        fixture.WriteDatabase();
        fixture.WriteAppliedHashes();
        File.Delete(Path.Combine(fixture.Context.SourceRoot, "Blocks", "Gone.xml"));

        await fixture.Coordinator.UpdateKnowledgeAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(new[] { "ingest_source" }, fixture.Knowledge.Calls);
        Assert.Equal(DeviceKnowledgeStatus.CurrentState, fixture.ReadStatus().State);
        Assert.DoesNotContain(
            "Blocks/Gone.xml",
            fixture.ReadMetadata().Knowledge.AppliedOverlayHashes.Keys);
    }

    [Fact]
    public async Task UpdateReplacesAPlainContentChangeIncrementally()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteSource("Blocks/A.xml", "<old />");
        fixture.WriteDatabase();
        fixture.WriteAppliedHashes();
        fixture.WriteSource("Blocks/A.xml", "<new />");

        await fixture.Coordinator.UpdateKnowledgeAsync(fixture.Context, CancellationToken.None);

        Assert.Equal(new[] { "update_components" }, fixture.Knowledge.Calls);
        Assert.Equal(DeviceKnowledgeStatus.CurrentState, fixture.ReadStatus().State);
    }

    [Fact]
    public void ReadingTheStatusWritesNothing()
    {
        var fixture = Fixture.Create(root);
        fixture.WriteSource("Blocks/A.xml", "<old />");
        fixture.WriteDatabase();
        fixture.WriteAppliedHashes();
        fixture.WriteSource("Blocks/A.xml", "<new />");
        var deviceBefore = File.ReadAllBytes(fixture.DeviceMetadataPath);
        var databaseBefore = File.ReadAllBytes(fixture.Context.KnowledgeDbPath);

        var status = fixture.ReadStatus();

        Assert.Equal(DeviceKnowledgeStatus.StaleState, status.State);
        Assert.Equal(deviceBefore, File.ReadAllBytes(fixture.DeviceMetadataPath));
        Assert.Equal(databaseBefore, File.ReadAllBytes(fixture.Context.KnowledgeDbPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>One device with a source tree, a device.json whose knowledge state the test sets, and a
    /// coordinator over the real catalog/store/reconciler/resolver.</summary>
    private sealed class Fixture
    {
        private const string DeviceId = "dev-1";

        private Fixture(DeviceContext context, AtomicJsonStore store, FakeToolCaller knowledge)
        {
            Context = context;
            this.store = store;
            Knowledge = knowledge;
            DeviceMetadataPath = Path.Combine(context.DeviceRoot, "device.json");
            Coordinator = new WorkbenchCoordinator(
                new FakeToolCaller(),
                knowledge,
                new FakeToolCaller(),
                new WorkbenchCatalog(new AtomicJsonStore(), Path.Combine(context.WorkbenchRoot, "catalog")),
                store,
                new DeviceReconciler(),
                new DeviceSourceResolver(_ => { }));
        }

        private readonly AtomicJsonStore store;

        public DeviceContext Context { get; }

        public FakeToolCaller Knowledge { get; }

        public string DeviceMetadataPath { get; }

        public WorkbenchCoordinator Coordinator { get; }

        public static Fixture Create(string parent, bool baselineStale = false)
        {
            var context = WorkbenchPaths.ResolveDevice(
                "wb-1", Path.Combine(parent, Guid.NewGuid().ToString("N")),
                "wt-1", "master", DeviceId, "PLC_1");
            Directory.CreateDirectory(context.SourceRoot);
            Directory.CreateDirectory(context.StagingRoot);
            var store = new AtomicJsonStore();
            // The knowledge server the coordinator talks to: an ingest writes the database file the
            // coordinator then treats as existing, and a partial update answers with its own result
            // shape. Either call is recorded, so a test can prove which repair ran.
            var knowledge = new FakeToolCaller()
                .Respond("ingest_source", _ =>
                {
                    File.WriteAllText(context.KnowledgeDbPath, "knowledge");
                    return new IngestResult { DbPath = context.KnowledgeDbPath };
                })
                .Respond("update_components", _ => new KnowledgeUpdateResult(
                    context.KnowledgeDbPath,
                    Array.Empty<string>(),
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    Array.Empty<string>()));
            var fixture = new Fixture(context, store, knowledge);
            fixture.WriteMetadata(baselineStale, new Dictionary<string, string>(StringComparer.Ordinal));
            return fixture;
        }

        public DeviceKnowledgeStatus ReadStatus() => Coordinator.ReadKnowledgeStatus(Context);

        public DeviceMetadata ReadMetadata() => store.Read<DeviceMetadata>(DeviceMetadataPath);

        public void WriteSource(string relative, string content)
        {
            var path = Path.Combine(Context.SourceRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void WriteDatabase() => File.WriteAllText(Context.KnowledgeDbPath, "knowledge");

        /// <summary>Persist the applied hashes for the named source files, or for every file the source
        /// tree currently holds — the bookkeeping a successful update leaves behind.</summary>
        public void WriteAppliedHashes(params string[] relativePaths)
        {
            var paths = relativePaths.Length > 0
                ? relativePaths
                : Directory
                    .EnumerateFiles(Context.SourceRoot, "*.xml", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(Context.SourceRoot, path).Replace('\\', '/'))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
            var hashes = paths.ToDictionary(
                path => path,
                path => Convert.ToHexString(SHA256.HashData(
                    File.ReadAllBytes(Path.Combine(Context.SourceRoot, path)))).ToLowerInvariant(),
                StringComparer.Ordinal);
            WriteMetadata(
                store.Read<DeviceMetadata>(DeviceMetadataPath).Knowledge.BaselineStale,
                hashes);
        }

        private void WriteMetadata(bool baselineStale, IReadOnlyDictionary<string, string> hashes) =>
            store.Write(
                DeviceMetadataPath,
                new DeviceMetadata(
                    WorkbenchSchema.CurrentVersion,
                    DeviceId,
                    "wt-1",
                    "PLC_1",
                    "PLC_1",
                    null,
                    null,
                    null,
                    new KnowledgeState(
                        Stale: false,
                        hashes,
                        "2026-07-29T00:00:00Z",
                        BaselineStale: baselineStale),
                    Array.Empty<DeviceImportRecord>()));
    }
}
