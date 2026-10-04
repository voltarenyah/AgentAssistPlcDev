using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// ADR-0012, Phase 4 (AC-004, AC-007): the write points invalidate a device's projection; the selection
/// boundary compares the stored manifest digest — every projected manifest field, not <c>contentHash</c>
/// alone — and re-projects before serving; the reconciliation pass re-projects a device an event flagged
/// and removes the facts of worktrees that no longer exist without taking the facts a device another
/// worktree still owns.
/// </summary>
public sealed class EngineeringGraphFreshnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"engineering-graph-freshness-{Guid.NewGuid():N}");

    /// <summary>AC-004: an event-flagged projection is re-projected before the read is served, and the
    /// read reflects the new content.</summary>
    [Fact]
    public void SelectionBoundaryReprojectsAnInvalidatedProjectionBeforeServing()
    {
        using var fixture = new FreshnessFixture(_root);
        var reader = new DeviceSnapshotGraphReader(fixture.Graph);
        Assert.Equal("Main", Assert.Single(reader.Read(fixture.Primary.Context, fixture.Primary.Metadata).SourceObjects).Name);
        Assert.Equal(
            ProjectionBoundaryReason.None,
            reader.EnsureProjectionCurrent(fixture.Primary.Context, fixture.Primary.Metadata).Reason);

        // The exported source changed and a write point (source apply, commit, branch switch, staging)
        // said so. The manifest's projected fields did not move, so the flag alone must drive this.
        fixture.WriteManifest(fixture.Primary, ManifestState.Default with { Name = "Renamed" });
        Assert.True(fixture.Graph.InvalidateDeviceProjection(fixture.DeviceId));

        var boundary = reader.EnsureProjectionCurrent(fixture.Primary.Context, fixture.Primary.Metadata);

        Assert.Equal(ProjectionBoundaryReason.Invalidated, boundary.Reason);
        Assert.True(boundary.Reprojected);
        Assert.False(Property(fixture.Graph, fixture.DeviceId, DevicePropertyNames.ProjectionInvalidated).Flag);
        Assert.Equal("Renamed", Assert.Single(reader.Read(fixture.Primary.Context, fixture.Primary.Metadata).SourceObjects).Name);
    }

    /// <summary>AC-004: every projected manifest field moves the digest, so a projected fact cannot go
    /// stale while the digest agrees. <c>contentHash</c> is one of them, not the only one.</summary>
    [Fact]
    public void DigestMovesForEveryProjectedManifestFieldAndNotOnlyTheContentHash()
    {
        using var fixture = new FreshnessFixture(_root);
        var baseline = fixture.Project(fixture.Primary, ManifestState.Default).ManifestDigest;

        var variations = new Dictionary<string, ManifestState>(StringComparer.Ordinal)
        {
            ["contentHash"] = ManifestState.Default with { ContentHash = "HASH-B" },
            ["status"] = ManifestState.Default with { Status = "Modified" },
            ["modifiedDate"] = ManifestState.Default with { ModifiedDate = "2026-08-01T09:00:00.0000000+00:00" },
            ["fingerprints"] = ManifestState.Default with
            {
                Fingerprints = new Dictionary<string, string> { ["Code"] = "BBBB2222" },
            },
            ["isKnowHowProtected"] = ManifestState.Default with { IsKnowHowProtected = true },
            ["name"] = ManifestState.Default with { Name = "Renamed" },
            ["number"] = ManifestState.Default with { Number = 7 },
            ["category"] = ManifestState.Default with { Category = "FC" },
            ["programmingLanguage"] = ManifestState.Default with { ProgrammingLanguage = "FBD" },
            ["exportedFile"] = ManifestState.Default with { ExportedFile = "Blocks/Area/Other [FC7].xml" },
            // Only the evidence kind is projected from `siemensTypeName`, so this is the variation that
            // moves the digest: an instance DB is a different projected fact from a standard block.
            ["siemensTypeName"] = ManifestState.Default with { SiemensTypeName = "InstanceDB" },
        };
        // The manifest's own `sourcePath` is deliberately not projected (the group path is derived from
        // the exported file), so it is not part of the digest either — the digest covers exactly the
        // fields the projection keeps.

        foreach (var (field, state) in variations)
        {
            var digest = fixture.Project(fixture.Primary, state).ManifestDigest;
            Assert.True(
                digest != baseline,
                $"Changing the projected manifest field '{field}' left the manifest digest at its previous value.");
        }

        // The export root is part of the digest too: the same manifest served from another worktree's
        // export root is a different input.
        var second = fixture.AddWorktree("wt-2", "feature");
        Assert.NotEqual(baseline, fixture.Project(second, ManifestState.Default).ManifestDigest);
    }

    /// <summary>AC-004: with no event at all, the boundary compares the stored digest with the manifest
    /// on disk — a change to a projected field that is not the exported content is caught and
    /// re-projected before serving.</summary>
    [Fact]
    public void SelectionBoundaryReprojectsWhenOnlyTheStatusChangedAndNoEventFired()
    {
        using var fixture = new FreshnessFixture(_root);
        var reader = new DeviceSnapshotGraphReader(fixture.Graph);
        fixture.WriteManifest(fixture.Primary, ManifestState.Default);
        reader.Read(fixture.Primary.Context, fixture.Primary.Metadata);
        var digestBefore = Property(fixture.Graph, fixture.DeviceId, DevicePropertyNames.ProjectionManifestDigest).Text;

        // An out-of-app edit: the status moves, the exported content does not.
        fixture.WriteManifest(fixture.Primary, ManifestState.Default with { Status = "Modified" });

        var boundary = reader.EnsureProjectionCurrent(fixture.Primary.Context, fixture.Primary.Metadata);

        Assert.Equal(ProjectionBoundaryReason.Digest, boundary.Reason);
        // The block's status row and the device's own digest row move; nothing else does.
        Assert.Equal(2, boundary.Projection!.Write.Updated);
        Assert.Equal(0, boundary.Projection.Write.Inserted);
        Assert.NotEqual(digestBefore, Property(fixture.Graph, fixture.DeviceId, DevicePropertyNames.ProjectionManifestDigest).Text);
        Assert.Equal(
            "Modified",
            Property(fixture.Graph, $"{fixture.DeviceId}:block-1", SourceObjectPropertyNames.Status).Text);
        Assert.Equal(
            "HASH-A",
            Property(fixture.Graph, $"{fixture.DeviceId}:block-1", SourceObjectPropertyNames.ContentHash).Text);

        // The next check finds a manifest that agrees again: it reads the export once and writes nothing.
        var settled = reader.EnsureProjectionCurrent(fixture.Primary.Context, fixture.Primary.Metadata);
        Assert.Equal(ProjectionBoundaryReason.None, settled.Reason);
        Assert.False(settled.Reprojected);
    }

    /// <summary>AC-004: a failed re-projection is reported as a projection failure, never served as the
    /// stale facts it was meant to replace.</summary>
    [Fact]
    public void AFailedReprojectionIsReportedAsAProjectionFailureAndServesNoStaleFacts()
    {
        using var fixture = new FreshnessFixture(_root);
        var reader = new DeviceSnapshotGraphReader(fixture.Graph);
        fixture.WriteManifest(fixture.Primary, ManifestState.Default);
        reader.Read(fixture.Primary.Context, fixture.Primary.Metadata);

        fixture.WriteManifest(fixture.Primary, ManifestState.Default with { Name = "Renamed" });
        // Every read still works; only the projection's write fails.
        fixture.Execute("""
            CREATE TRIGGER refuse_projection_write BEFORE INSERT ON graph_entity_properties
            BEGIN SELECT RAISE(ABORT, 'projection write refused'); END;
            """);

        var failure = Assert.Throws<EngineeringGraphProjectionException>(
            () => reader.Read(fixture.Primary.Context, fixture.Primary.Metadata));
        Assert.Equal(EngineeringGraphProjectionException.FailureCode, failure.Code);
        Assert.Contains(fixture.DeviceId, failure.Message, StringComparison.Ordinal);

        fixture.Execute("DROP TRIGGER refuse_projection_write;");
        Assert.Equal(
            "Renamed",
            Assert.Single(reader.Read(fixture.Primary.Context, fixture.Primary.Metadata).SourceObjects).Name);
    }

    /// <summary>AC-007: deleting a worktree leaves no node, property row or edge of it, and a device
    /// another worktree still registers keeps its facts.</summary>
    [Fact]
    public void WorktreeDeletionRemovesItsFactsAndKeepsADeviceAnotherWorktreeOwns()
    {
        using var fixture = new FreshnessFixture(_root);
        var second = fixture.AddWorktree("wt-2", "feature");
        fixture.Project(fixture.Primary, ManifestState.Default);
        // A commit and a conversation of the second worktree: its node, its properties and its edge go
        // with the worktree. The conversation carries the shared device id, so it is the case that
        // proves the retained device does not drag another worktree's own nodes along with it.
        fixture.Graph.RegisterEntity(new GraphEntity(
            GraphEntityKind.GitCommit, "sha-wt2", fixture.WorkbenchId, second.Context.WorktreeId));
        fixture.Graph.RegisterEntity(new GraphEntity(
            GraphEntityKind.Session, "session-wt2", fixture.WorkbenchId, second.Context.WorktreeId, fixture.DeviceId));
        fixture.Graph.AddEdge(GraphEntityKind.GitCommit, "sha-wt2", GraphEntityKind.SourceObject,
            $"{fixture.DeviceId}:block-1", GraphProvenance.Evidence);
        // The same device id is served from the second worktree, which adds it to the owning set.
        fixture.Project(second, ManifestState.Default);

        Assert.Equal(
            new[] { fixture.Primary.Context.WorktreeId, second.Context.WorktreeId },
            Owners(fixture));
        Assert.Equal(2, fixture.Count("SELECT COUNT(*) FROM graph_entities WHERE worktree_id = $wt",
            ("$wt", second.Context.WorktreeId)));
        fixture.Graph.InvalidateWorktreeProjections(second.Context.WorktreeId);

        // The catalog no longer registers the second worktree.
        fixture.Unregister(second.Context.WorktreeId);
        var cleanup = new EngineeringGraphReconciliation(fixture.Graph).RemoveDeletedWorktreeFacts();

        Assert.Equal(new[] { second.Context.WorktreeId }, cleanup.RemovedWorktrees);
        Assert.Empty(cleanup.DevicesRemoved);
        Assert.Equal(new[] { fixture.DeviceId }, cleanup.DevicesRetained);
        // The two nodes the deleted worktree owned, and the edge that referenced one of them. The shared
        // device's property rows are kept, because a worktree that still exists owns them.
        Assert.Equal(2, cleanup.NodesRemoved);
        Assert.Equal(0, cleanup.PropertyRowsRemoved);
        Assert.Equal(1, cleanup.EdgeRowsRemoved);
        Assert.Null(fixture.Graph.GetEntity(GraphEntityKind.Session, "session-wt2"));
        Assert.Null(fixture.Graph.GetEntity(GraphEntityKind.GitCommit, "sha-wt2"));
        Assert.Equal(0, fixture.Count("SELECT COUNT(*) FROM graph_entities WHERE worktree_id = $wt",
            ("$wt", second.Context.WorktreeId)));
        Assert.Equal(0, fixture.Count("""
            SELECT COUNT(*) FROM graph_entity_properties property
            WHERE NOT EXISTS (
                SELECT 1 FROM graph_entities entity
                WHERE entity.entity_kind = property.entity_kind AND entity.entity_id = property.entity_id);
            """));
        Assert.Equal(0, fixture.Count("""
            SELECT COUNT(*) FROM graph_edges edge
            WHERE NOT EXISTS (
                SELECT 1 FROM graph_entities entity
                WHERE (entity.entity_kind = edge.from_kind AND entity.entity_id = edge.from_id)
                   OR (entity.entity_kind = edge.to_kind AND entity.entity_id = edge.to_id));
            """));
        Assert.Equal(0, fixture.Graph.CountEdges());

        // The device's facts survive: the node, its property rows and its owning set without the
        // deleted worktree.
        var device = fixture.Graph.GetEntity(GraphEntityKind.Device, fixture.DeviceId);
        Assert.NotNull(device);
        Assert.Equal(fixture.Primary.Context.WorktreeId, device!.WorktreeId);
        Assert.NotEmpty(fixture.Graph.GetProperties(GraphEntityKind.Device, fixture.DeviceId));
        Assert.Equal(new[] { fixture.Primary.Context.WorktreeId }, Owners(fixture));
        Assert.Equal(
            "Main",
            Property(fixture.Graph, $"{fixture.DeviceId}:block-1", SourceObjectPropertyNames.Name).Text);
        // The invalidation the deleted worktree carried stays on the retained device, so the surviving
        // worktree's next read re-projects instead of serving content from a dead worktree.
        Assert.True(Property(fixture.Graph, fixture.DeviceId, DevicePropertyNames.ProjectionInvalidated).Flag);

        // A second run, with the workbench's remaining worktree still registered, removes nothing.
        Assert.Empty(fixture.Graph.RemoveUnregisteredWorktreeFacts().RemovedWorktrees);
    }

    /// <summary>A device whose last owning worktree is deleted takes its facts with it.</summary>
    [Fact]
    public void WorktreeDeletionRemovesTheFactsOfADeviceNoOtherWorktreeOwns()
    {
        using var fixture = new FreshnessFixture(_root);
        fixture.Project(fixture.Primary, ManifestState.Default);
        var nodesBefore = fixture.Count("SELECT COUNT(*) FROM graph_entities;");
        Assert.True(nodesBefore > 1);

        fixture.Unregister(fixture.Primary.Context.WorktreeId);
        var cleanup = fixture.Graph.RemoveUnregisteredWorktreeFacts();

        Assert.Equal(new[] { fixture.Primary.Context.WorktreeId }, cleanup.RemovedWorktrees);
        Assert.Equal(new[] { fixture.DeviceId }, cleanup.DevicesRemoved);
        Assert.Empty(cleanup.DevicesRetained);
        Assert.Equal(nodesBefore, cleanup.NodesRemoved);
        Assert.True(cleanup.PropertyRowsRemoved > 0);
        Assert.Equal(0, fixture.Count("SELECT COUNT(*) FROM graph_entities;"));
        Assert.Equal(0, fixture.Count("SELECT COUNT(*) FROM graph_entity_properties;"));
    }

    /// <summary>AC-007: the graph refuses to clean up when it cannot know which worktrees are registered,
    /// because every worktree id would then look deleted.</summary>
    [Fact]
    public void WorktreeCleanupRequiresTheWorktreeRegistrations()
    {
        using var fixture = new FreshnessFixture(_root);
        fixture.Project(fixture.Primary, ManifestState.Default);
        var nodesBefore = fixture.Count("SELECT COUNT(*) FROM graph_entities;");

        var unreckoned = new EngineeringGraphService(fixture.Store, fixture.WorkbenchId);
        var failure = Assert.Throws<EngineeringGraphConstraintException>(() => unreckoned.RemoveUnregisteredWorktreeFacts());

        Assert.Equal("GRAPH_WORKTREE_REGISTRY_REQUIRED", failure.Code);
        Assert.Equal(nodesBefore, fixture.Count("SELECT COUNT(*) FROM graph_entities;"));
    }

    /// <summary>AC-004/ADR-0012 item 4: the repair pass re-projects a device an event flagged even when
    /// its digest still matches, and leaves an unflagged device alone.</summary>
    [Fact]
    public void RepairPassReprojectsAnInvalidatedDeviceWhoseDigestMatches()
    {
        using var fixture = new FreshnessFixture(_root);
        var reconciliation = new EngineeringGraphReconciliation(fixture.Graph);
        fixture.Project(fixture.Primary, ManifestState.Default);

        var untouched = reconciliation.RepairDevice(fixture.Primary.Context, fixture.Primary.Metadata);
        Assert.False(untouched.WasInvalidated);
        Assert.Null(untouched.Projection);

        fixture.Graph.InvalidateDeviceProjection(fixture.DeviceId);
        var repaired = reconciliation.RepairDevice(fixture.Primary.Context, fixture.Primary.Metadata);

        Assert.True(repaired.WasInvalidated);
        Assert.True(repaired.DigestMatched);
        Assert.NotNull(repaired.Projection);
        Assert.Equal(1, repaired.Projection!.Write.Updated);
        Assert.False(Property(fixture.Graph, fixture.DeviceId, DevicePropertyNames.ProjectionInvalidated).Flag);
        // The facts themselves did not change, so nothing but the flag was rewritten.
        Assert.Equal(0, repaired.Projection.Write.Inserted);
        Assert.Equal(0, repaired.Projection.Write.Deleted);
    }

    /// <summary>AC-007: a device id registered in more than one worktree is not "last writer wins" —
    /// each projection adds its worktree to the owning set, and the entity row keeps the ordinal-first
    /// owner so it does not move with whoever projected last.</summary>
    [Fact]
    public void EachWorktreeAddsItselfToTheSharedDevicesOwningSet()
    {
        using var fixture = new FreshnessFixture(_root);
        var second = fixture.AddWorktree("wt-2", "feature");
        fixture.Project(fixture.Primary, ManifestState.Default);
        var firstOwner = fixture.Graph.GetEntity(GraphEntityKind.Device, fixture.DeviceId)!.WorktreeId;

        fixture.Project(second, ManifestState.Default);

        Assert.Equal(new[] { fixture.Primary.Context.WorktreeId, second.Context.WorktreeId }, Owners(fixture));
        Assert.Equal(fixture.Primary.Context.WorktreeId, firstOwner);
        Assert.Equal(
            firstOwner,
            fixture.Graph.GetEntity(GraphEntityKind.Device, fixture.DeviceId)!.WorktreeId);
        Assert.Equal(
            firstOwner,
            fixture.Graph.GetEntity(GraphEntityKind.SourceObject, $"{fixture.DeviceId}:block-1")!.WorktreeId);
    }

    /// <summary>AC-007: invalidating one worktree leaves the facts of another worktree alone.</summary>
    [Fact]
    public void InvalidatingOneWorktreeDoesNotFlagAnotherWorktreesFacts()
    {
        using var fixture = new FreshnessFixture(_root);
        fixture.Project(fixture.Primary, ManifestState.Default);

        Assert.Equal(0, fixture.Graph.InvalidateWorktreeProjections("wt-elsewhere"));
        Assert.False(Property(fixture.Graph, fixture.DeviceId, DevicePropertyNames.ProjectionInvalidated).Flag);
        Assert.Equal(1, fixture.Graph.InvalidateWorktreeProjections(fixture.Primary.Context.WorktreeId));
        Assert.True(Property(fixture.Graph, fixture.DeviceId, DevicePropertyNames.ProjectionInvalidated).Flag);
    }

    private static IReadOnlyList<string> Owners(FreshnessFixture fixture) =>
        EngineeringGraphProjectionService.ParseWorktrees(
            Property(fixture.Graph, fixture.DeviceId, DevicePropertyNames.ProjectionWorktrees).Json);

    private static GraphProperty Property(EngineeringGraphService graph, string entityId, string name) =>
        graph.GetProperties(GraphEntityKind.Device, entityId)
            .Concat(graph.GetProperties(GraphEntityKind.SourceObject, entityId))
            .Single(property => property.Name == name);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Every projected manifest field of one exported block, so a test can vary exactly one of
    /// them and prove the digest followed.</summary>
    private sealed record ManifestState(
        string Name = "Main",
        string Status = "Exported",
        int? Number = 1,
        string Category = "OB",
        string? ProgrammingLanguage = "LAD",
        string? SourcePath = "Area/Main",
        string? ExportedFile = "Blocks/Area/Main [OB1].xml",
        string? ContentHash = "HASH-A",
        string? SiemensTypeName = "SW.Blocks.OB",
        bool? IsKnowHowProtected = null,
        string? ModifiedDate = "2026-07-20T10:00:00.0000000+00:00",
        IReadOnlyDictionary<string, string>? Fingerprints = null)
    {
        public static ManifestState Default { get; } = new(
            Fingerprints: new Dictionary<string, string> { ["Code"] = "AAAA1111" });

        /// <summary>Writes this state's manifest into the worktree's device source root.</summary>
        public void Write(DeviceContext context)
        {
            Directory.CreateDirectory(context.SourceRoot);
            File.WriteAllText(
                Path.Combine(context.SourceRoot, "metadata.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    device = new { plcName = "PLC_1" },
                    components = new object[]
                    {
                        new
                        {
                            id = "block-1",
                            name = Name,
                            sourcePath = SourcePath,
                            category = Category,
                            status = Status,
                            exportedFile = ExportedFile,
                            number = Number,
                            programmingLanguage = ProgrammingLanguage,
                            contentHash = ContentHash,
                            siemensTypeName = SiemensTypeName,
                            isKnowHowProtected = IsKnowHowProtected,
                            modifiedDate = ModifiedDate,
                            fingerprints = Fingerprints,
                        },
                    },
                }));
        }
    }

    /// <summary>One workbench root and one device id, plus the worktrees that register it.</summary>
    private sealed class FreshnessFixture : IDisposable
    {
        private readonly HashSet<string> _registered = new(StringComparer.Ordinal);

        public FreshnessFixture(string root)
        {
            Root = root;
            Store = new EngineeringGraphStore(root);
            Graph = new EngineeringGraphService(Store, WorkbenchId, _registered.Contains);
            Primary = AddWorktree("wt-1", "main");
            WriteManifest(Primary, ManifestState.Default);
        }

        public string Root { get; }
        public string WorkbenchId => "wb-1";
        public string DeviceId => "plc-1";
        public EngineeringGraphStore Store { get; }
        public EngineeringGraphService Graph { get; }
        public Worktree Primary { get; }

        public Worktree AddWorktree(string worktreeId, string relativePath)
        {
            _registered.Add(worktreeId);
            var worktreeRoot = Path.Combine(Root, "worktrees", relativePath);
            var deviceRoot = Path.Combine(worktreeRoot, "devices", DeviceId);
            var sourceRoot = Path.Combine(deviceRoot, "source");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(Path.Combine(deviceRoot, "staging"));
            var context = new DeviceContext(WorkbenchId, worktreeId, DeviceId, Root, worktreeRoot, deviceRoot,
                sourceRoot, Path.Combine(deviceRoot, "staging"), Path.Combine(deviceRoot, "plc-knowledge.db"));
            var metadata = new DeviceMetadata(
                WorkbenchSchema.CurrentVersion, DeviceId, worktreeId, "PLC 1", "engineering-plc-1",
                null, null, null,
                new KnowledgeState(false, new Dictionary<string, string>(), "2026-07-29T08:00:00Z", false),
                []);
            return new Worktree(context, metadata);
        }

        /// <summary>The catalog no longer registers a worktree: the input of the deletion cleanup.</summary>
        public void Unregister(string worktreeId) => _registered.Remove(worktreeId);

        public void WriteManifest(Worktree worktree, ManifestState state) => state.Write(worktree.Context);

        public DeviceProjectionResult Project(Worktree worktree, ManifestState state)
        {
            WriteManifest(worktree, state);
            return new EngineeringGraphProjectionService(Graph).ProjectDevice(worktree.Context, worktree.Metadata);
        }

        public int Count(string sql, params (string Name, object Value)[] parameters)
        {
            using var command = Store.Connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public void Execute(string sql)
        {
            using var command = Store.Connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose() => Store.Dispose();
    }

    private sealed record Worktree(DeviceContext Context, DeviceMetadata Metadata);
}
