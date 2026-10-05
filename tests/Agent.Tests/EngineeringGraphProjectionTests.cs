using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Contracts.Engineering;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// ADR-0011/ADR-0012, Phase 1: the manifest is the ingest source, facts become node properties, the
/// ingest hash-diffs (an unchanged run writes nothing), a removed component loses its rows and the
/// edges that reference it, and a crawl-derived object the crawl cannot classify stays unclassified.
/// </summary>
public sealed class EngineeringGraphProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"engineering-graph-projection-{Guid.NewGuid():N}");

    [Fact]
    public void ProjectsManifestFactsAndASecondIngestWritesNothing()
    {
        var fixture = new ProjectionFixture(_root);
        fixture.WriteManifest(
            DeviceSection(),
            Component("ob-1", "Main", "OB", "Blocks/Area/Main [OB1].xml", 1, "LAD", "Area/Main",
                contentHash: "HASH-OB", siemensTypeName: "OB", isKnowHowProtected: true, modifiedDate: "2026-07-20T10:00:00.0000000+00:00",
                fingerprints: new Dictionary<string, string> { ["Interface"] = "BBBB2222", ["Code"] = "AAAA1111" }),
            Component("db-1", "PC_Clock", "DB", "DB/00_Common_Part/PC_Clock [DB2].xml", 2, "DB", "00_Common_Part/PC_Clock",
                contentHash: "HASH-DB", siemensTypeName: "InstanceDB"),
            Component("tags-1", "Inputs", "Tags", "Tags/LineA/Inputs.xml", null, null, "LineA/Inputs", contentHash: "HASH-TAGS"));
        fixture.WriteWorktreeMetadata("C:/demo/plc");
        File.WriteAllBytes(fixture.Context.KnowledgeDbPath, [1]);

        using var store = new EngineeringGraphStore(_root);
        var service = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var projection = new EngineeringGraphProjectionService(service);

        var result = projection.ProjectDevice(fixture.Context, fixture.Metadata);

        Assert.False(result.UsedCrawlFallback);
        Assert.Equal(3, result.SourceObjectCount);
        // The device page counts block-category objects only (Blocks/ and DB/), as the crawl always has.
        Assert.Equal(2, result.BlockCount);
        Assert.True(result.PropertyRowsWritten > 0);
        Assert.Empty(result.Diagnostics);

        var device = Properties(service.GetProperties(GraphEntityKind.Device, "plc-1"));
        Assert.Equal("wb-1", device[DevicePropertyNames.WorkbenchId].Text);
        Assert.Equal("wt-1", device[DevicePropertyNames.WorktreeId].Text);
        Assert.Equal("plc-1", device[DevicePropertyNames.DeviceId].Text);
        Assert.Equal("PLC 1", device[DevicePropertyNames.PlcName].Text);
        Assert.Equal("engineering-plc-1", device[DevicePropertyNames.EngineeringIdentity].Text);
        Assert.Equal(fixture.Context.SourceRoot, device[DevicePropertyNames.SourceRoot].Text);
        Assert.Equal("C:/demo/plc", device[DevicePropertyNames.SourceProjectPath].Text);
        Assert.Equal("current", device[DevicePropertyNames.KnowledgeState].Text);
        Assert.Equal("2026-07-29T08:00:00Z", device[DevicePropertyNames.KnowledgeUpdatedAt].Text);
        Assert.Equal(2d, device[DevicePropertyNames.BlockCount].Number);
        Assert.Equal(3d, device[DevicePropertyNames.SourceObjectCount].Number);
        Assert.Equal("[]", device[DevicePropertyNames.Diagnostics].Json);
        Assert.False(device[DevicePropertyNames.ProjectionInvalidated].Flag);
        Assert.Equal(result.ManifestDigest, device[DevicePropertyNames.ProjectionManifestDigest].Text);
        Assert.Equal("PLC_1", device[DevicePropertyNames.ExportPlcName].Text);
        Assert.Equal("Station_1", device[DevicePropertyNames.ExportDeviceName].Text);
        Assert.Equal("OrderNumber:6ES7515-2AM02-0AB0/V2.9", device[DevicePropertyNames.ExportTypeIdentifier].Text);
        Assert.Equal("TestPLCExportDemo", device[DevicePropertyNames.ExportProjectName].Text);
        Assert.Equal("Ansel", device[DevicePropertyNames.ExportProjectAuthor].Text);
        Assert.Equal("demo project", device[DevicePropertyNames.ExportProjectComment].Text);
        Assert.Equal("V17", device[DevicePropertyNames.ExportProjectVersion].Text);
        Assert.Null(device[DevicePropertyNames.ExportProjectCopyright].Text);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero), device[DevicePropertyNames.ExportProjectCreationTime].Timestamp);
        Assert.Equal(new DateTimeOffset(2026, 7, 30, 9, 30, 0, TimeSpan.Zero), device[DevicePropertyNames.ExportProjectLastModified].Timestamp);
        Assert.Equal("Ansel", device[DevicePropertyNames.ExportProjectLastModifiedBy].Text);
        Assert.True(device[DevicePropertyNames.ExportIsSafetyDevice].Flag);
        Assert.Equal("ok", device[DevicePropertyNames.ExportFSignatureReadState].Text);
        Assert.Equal("1A2B3C4D", device[DevicePropertyNames.ExportFSignature].Text);
        // All 14 DeviceExportMetadata fields, and nothing beyond them.
        Assert.Equal(14, device.Keys.Count(key => key.StartsWith("export.", StringComparison.Ordinal)));
        Assert.Equal(GraphPropertySource.DeviceProjection, device[DevicePropertyNames.PlcName].Source);

        var main = Properties(service.GetProperties(GraphEntityKind.SourceObject, "plc-1:ob-1"));
        Assert.Equal("ob-1", main[SourceObjectPropertyNames.Id].Text);
        Assert.Equal("Main", main[SourceObjectPropertyNames.Name].Text);
        Assert.Equal(1d, main[SourceObjectPropertyNames.Number].Number);
        Assert.Equal("OB", main[SourceObjectPropertyNames.Category].Text);
        Assert.Equal("LAD", main[SourceObjectPropertyNames.ProgrammingLanguage].Text);
        Assert.Equal("Area", main[SourceObjectPropertyNames.GroupPath].Text);
        Assert.Equal("Blocks/Area/Main [OB1].xml", main[SourceObjectPropertyNames.RelativePath].Text);
        Assert.Equal("HASH-OB", main[SourceObjectPropertyNames.ContentHash].Text);
        Assert.True(main[SourceObjectPropertyNames.IsKnowHowProtected].Flag);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero), main[SourceObjectPropertyNames.ModifiedDate].Timestamp);
        Assert.Equal("Exported", main[SourceObjectPropertyNames.Status].Text);
        Assert.Equal(ManagedSourceEvidenceKind.StandardBlock, main[SourceObjectPropertyNames.EvidenceKind].Text);
        // Fingerprint names are sorted, so the same set always serialises to the same text.
        Assert.Equal("""{"Code":"AAAA1111","Interface":"BBBB2222"}""", main[SourceObjectPropertyNames.Fingerprints].Json);
        Assert.True(main[SourceObjectPropertyNames.IsBlock].Flag);
        // AC-002 keeps today's value: the crawl has always produced false for every block.
        Assert.False(main[SourceObjectPropertyNames.Modified].Flag);
        Assert.Equal("Blocks/Area/Main [OB1].xml",
            service.GetEntity(GraphEntityKind.SourceObject, "plc-1:ob-1")!.ExternalRef);

        // The block subset is a property of the source-object rows, not a second node kind.
        Assert.True(Properties(service.GetProperties(GraphEntityKind.SourceObject, "plc-1:db-1"))[SourceObjectPropertyNames.IsBlock].Flag);
        Assert.False(Properties(service.GetProperties(GraphEntityKind.SourceObject, "plc-1:tags-1"))[SourceObjectPropertyNames.IsBlock].Flag);

        var second = projection.ProjectDevice(fixture.Context, fixture.Metadata);

        Assert.Equal(result.ManifestDigest, second.ManifestDigest);
        Assert.Equal(0, second.PropertyRowsWritten);
        Assert.Equal(new GraphPropertyWriteResult(0, 0, 0, 0), second.Write);
        Assert.Equal(3, second.SourceObjectCount);
    }

    [Fact]
    public void RewritesOnlyTheChangedComponentWhenItsContentHashChanges()
    {
        var fixture = new ProjectionFixture(_root);
        fixture.WriteManifest(
            DeviceSection(),
            Component("ob-1", "Main", "OB", "Blocks/Area/Main [OB1].xml", 1, "LAD", "Area/Main", contentHash: "HASH-A", siemensTypeName: "OB"),
            Component("tags-1", "Inputs", "Tags", "Tags/LineA/Inputs.xml", null, null, "LineA/Inputs", contentHash: "HASH-TAGS"));

        using var store = new EngineeringGraphStore(_root);
        var service = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var projection = new EngineeringGraphProjectionService(service);
        projection.ProjectDevice(fixture.Context, fixture.Metadata);
        var before = ReadRows(store);

        // Only the first component's exported content changed.
        fixture.WriteManifest(
            DeviceSection(),
            Component("ob-1", "Main", "OB", "Blocks/Area/Main [OB1].xml", 1, "LAD", "Area/Main", contentHash: "HASH-B", siemensTypeName: "OB"),
            Component("tags-1", "Inputs", "Tags", "Tags/LineA/Inputs.xml", null, null, "LineA/Inputs", contentHash: "HASH-TAGS"));

        var result = projection.ProjectDevice(fixture.Context, fixture.Metadata);
        var after = ReadRows(store);
        var changed = before.Keys.Union(after.Keys)
            .Where(key => !before.TryGetValue(key, out var old) || !after.TryGetValue(key, out var updated) || old != updated)
            .ToArray();

        // The changed component's content hash and the device's manifest digest move; nothing else does.
        Assert.Equal(2, changed.Length);
        Assert.Contains(("source_object", "plc-1:ob-1", SourceObjectPropertyNames.ContentHash), changed);
        Assert.Contains(("device", "plc-1", DevicePropertyNames.ProjectionManifestDigest), changed);
        Assert.Equal(2, result.PropertyRowsWritten);
        Assert.Equal(2, result.Write.Updated);
        Assert.Equal(0, result.Write.Inserted);
        Assert.Equal(0, result.Write.Deleted);
        Assert.Equal(0, result.Write.NodesRemoved);

        // The other component keeps exactly the rows the first ingest wrote.
        foreach (var key in before.Keys.Where(key => key.Id == "plc-1:tags-1"))
            Assert.Equal(before[key], after[key]);
    }

    [Fact]
    public void DeletesARemovedComponentsRowsAndTheEdgesThatReferenceIt()
    {
        var fixture = new ProjectionFixture(_root);
        fixture.WriteManifest(
            DeviceSection(),
            Component("ob-1", "Main", "OB", "Blocks/Area/Main [OB1].xml", 1, "LAD", "Area/Main", contentHash: "HASH-A", siemensTypeName: "OB"),
            Component("tags-1", "Inputs", "Tags", "Tags/LineA/Inputs.xml", null, null, "LineA/Inputs", contentHash: "HASH-TAGS"));

        using var store = new EngineeringGraphStore(_root);
        var service = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var projection = new EngineeringGraphProjectionService(service);
        projection.ProjectDevice(fixture.Context, fixture.Metadata);

        // A commit points at the block, so the removal must take its edges with it (the graph has no
        // foreign key on graph_edges).
        service.RegisterEntity(new GraphEntity(GraphEntityKind.GitCommit, "sha-1", "wb-1", "wt-1"));
        service.AddEdge(GraphEntityKind.GitCommit, "sha-1", GraphEntityKind.SourceObject, "plc-1:ob-1", GraphProvenance.Evidence);
        Assert.Equal(1, service.CountEdges());

        // The component disappears from the export.
        fixture.WriteManifest(
            DeviceSection(),
            Component("tags-1", "Inputs", "Tags", "Tags/LineA/Inputs.xml", null, null, "LineA/Inputs", contentHash: "HASH-TAGS"));

        var result = projection.ProjectDevice(fixture.Context, fixture.Metadata);

        Assert.Equal(1, result.Write.NodesRemoved);
        Assert.True(result.Write.Deleted > 0);
        Assert.Null(service.GetEntity(GraphEntityKind.SourceObject, "plc-1:ob-1"));
        Assert.Empty(service.GetProperties(GraphEntityKind.SourceObject, "plc-1:ob-1"));
        Assert.Equal(0, Scalar(store.Connection, "SELECT COUNT(*) FROM graph_entity_properties WHERE entity_id = 'plc-1:ob-1';"));
        Assert.Equal(0, service.CountEdges());

        // The component that stayed keeps its node and its facts.
        Assert.NotNull(service.GetEntity(GraphEntityKind.SourceObject, "plc-1:tags-1"));
        Assert.Equal("Inputs", Properties(service.GetProperties(GraphEntityKind.SourceObject, "plc-1:tags-1"))[SourceObjectPropertyNames.Name].Text);
        Assert.Equal(1d, Properties(service.GetProperties(GraphEntityKind.Device, "plc-1"))[DevicePropertyNames.SourceObjectCount].Number);
    }

    [Fact]
    public void MarksObjectsTheCrawlCannotClassifyUnclassifiedWhenTheManifestIsMissing()
    {
        var fixture = new ProjectionFixture(_root);
        fixture.WriteSource("Blocks/Area/Main [OB1].xml", BlockXml("SW.Blocks.OB", "Main", 1, "LAD"));
        fixture.WriteSource("DB/Area/Data [DB4].xml", BlockXml("SW.Blocks.InstanceDB", "Data", 4, "DB"));

        using var store = new EngineeringGraphStore(_root);
        var service = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var projection = new EngineeringGraphProjectionService(service);

        var result = projection.ProjectDevice(fixture.Context, fixture.Metadata);

        Assert.True(result.UsedCrawlFallback);
        Assert.Equal(2, result.SourceObjectCount);
        Assert.Equal(2, result.BlockCount);
        Assert.Contains(result.Diagnostics, message =>
            message.Contains("metadata.json", StringComparison.Ordinal)
            && message.Contains("crawl", StringComparison.OrdinalIgnoreCase));

        var device = Properties(service.GetProperties(GraphEntityKind.Device, "plc-1"));
        // The device page's diagnostics keep today's meaning: the crawl's own diagnostics, not the
        // ingest's note about why the crawl ran. The note stays in the projection's report (asserted
        // above), so AC-002's field-for-field comparison of the page's diagnostics holds.
        Assert.Equal("[]", device[DevicePropertyNames.Diagnostics].Json);
        Assert.Empty(result.StoredDiagnostics);
        // A legacy manifest has no device section, so no export field is projected.
        Assert.DoesNotContain(DevicePropertyNames.ExportPlcName, device.Keys);

        // The crawl sees only the generic "DB" element, so the object stays unclassified instead of
        // being claimed as a standard block: the picker excludes instance DBs by kind.
        var instanceDb = Properties(service.GetProperties(GraphEntityKind.SourceObject, "plc-1:source:DB/Area/Data [DB4].xml"));
        Assert.Equal("DB", instanceDb[SourceObjectPropertyNames.Category].Text);
        Assert.Equal(GraphPropertyValueKind.Text, instanceDb[SourceObjectPropertyNames.EvidenceKind].Kind);
        Assert.Null(instanceDb[SourceObjectPropertyNames.EvidenceKind].Text);
        // The crawl carries no manifest-only metadata: know-how protection stays unknown.
        Assert.Equal(GraphPropertyValueKind.Flag, instanceDb[SourceObjectPropertyNames.IsKnowHowProtected].Kind);
        Assert.Null(instanceDb[SourceObjectPropertyNames.IsKnowHowProtected].Flag);

        var block = Properties(service.GetProperties(GraphEntityKind.SourceObject, "plc-1:source:Blocks/Area/Main [OB1].xml"));
        Assert.Equal(ManagedSourceEvidenceKind.StandardBlock, block[SourceObjectPropertyNames.EvidenceKind].Text);
    }

    private static Dictionary<string, GraphProperty> Properties(IReadOnlyList<GraphProperty> properties) =>
        properties.ToDictionary(property => property.Name, StringComparer.Ordinal);

    /// <summary>Every stored property row as one comparable string, so a test can prove which rows a
    /// later ingest rewrote.</summary>
    private static Dictionary<(string Kind, string Id, string Name), string> ReadRows(EngineeringGraphStore store)
    {
        using var command = store.Connection.CreateCommand();
        command.CommandText = """
            SELECT entity_kind, entity_id, name, value_kind, IFNULL(value_text,''), IFNULL(value_number,''),
                   IFNULL(value_flag,''), IFNULL(value_timestamp,''), IFNULL(value_json,''), source
            FROM graph_entity_properties;
            """;
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<(string, string, string), string>();
        while (reader.Read())
            rows[(reader.GetString(0), reader.GetString(1), reader.GetString(2))] = string.Join('|',
                Enumerable.Range(3, 7).Select(index => Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty));
        return rows;
    }

    private static object DeviceSection() => new
    {
        plcName = "PLC_1",
        deviceName = "Station_1",
        typeIdentifier = "OrderNumber:6ES7515-2AM02-0AB0/V2.9",
        projectName = "TestPLCExportDemo",
        projectAuthor = "Ansel",
        projectComment = "demo project",
        projectVersion = "V17",
        projectCopyright = (string?)null,
        projectCreationTime = "2026-07-01T08:00:00.0000000+00:00",
        projectLastModified = "2026-07-30T09:30:00.0000000+00:00",
        projectLastModifiedBy = "Ansel",
        isSafetyDevice = true,
        fSignatureReadState = "ok",
        fSignature = "1A2B3C4D",
    };

    private static object Component(
        string id,
        string name,
        string category,
        string exportedFile,
        int? number,
        string? language,
        string? sourcePath,
        string? contentHash = null,
        string? siemensTypeName = null,
        bool? isKnowHowProtected = null,
        string? modifiedDate = null,
        Dictionary<string, string>? fingerprints = null) =>
        new
        {
            id,
            name,
            sourcePath,
            category,
            status = "Exported",
            exportedFile,
            number,
            programmingLanguage = language,
            contentHash,
            siemensTypeName,
            isKnowHowProtected,
            modifiedDate,
            fingerprints,
        };

    private static string BlockXml(string elementName, string name, int number, string language) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <{elementName} ID="0">
            <AttributeList>
              <Name>{name}</Name>
              <Number>{number}</Number>
              <ProgrammingLanguage>{language}</ProgrammingLanguage>
            </AttributeList>
          </{elementName}>
        </Document>
        """;

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>One device's export root, shaped like the workbench's own layout.</summary>
    private sealed class ProjectionFixture
    {
        public ProjectionFixture(string root)
        {
            var worktreeRoot = Path.Combine(root, "worktrees", "main");
            var deviceRoot = Path.Combine(worktreeRoot, "devices", "plc-1");
            var sourceRoot = Path.Combine(deviceRoot, "source");
            var stagingRoot = Path.Combine(deviceRoot, "staging");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(stagingRoot);

            Context = new DeviceContext(
                "wb-1",
                "wt-1",
                "plc-1",
                root,
                worktreeRoot,
                deviceRoot,
                sourceRoot,
                stagingRoot,
                Path.Combine(deviceRoot, "plc-knowledge.db"));
            Metadata = new DeviceMetadata(
                WorkbenchSchema.CurrentVersion,
                "plc-1",
                "wt-1",
                "PLC 1",
                "engineering-plc-1",
                null,
                null,
                null,
                new KnowledgeState(false, new Dictionary<string, string>(), "2026-07-29T08:00:00Z", false),
                []);
        }

        public DeviceContext Context { get; }
        public DeviceMetadata Metadata { get; }

        public void WriteManifest(object device, params object[] components) =>
            File.WriteAllText(
                Path.Combine(Context.SourceRoot, "metadata.json"),
                JsonSerializer.Serialize(new { schemaVersion = "1.0", device, components }));

        public void WriteSource(string relativePath, string contents)
        {
            var path = WorkbenchPaths.ResolveRelative(Context.SourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void WriteWorktreeMetadata(string sourceProjectPath) =>
            new AtomicJsonStore().Write(
                Path.Combine(Context.WorktreeRoot, "worktree.json"),
                new WorktreeMetadata(
                    WorkbenchSchema.CurrentVersion,
                    "wt-1",
                    "wb-1",
                    "Main",
                    "master",
                    "2026-01-01T00:00:00.0000000+00:00",
                    null,
                    null,
                    sourceProjectPath,
                    ["plc-1"],
                    null));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
