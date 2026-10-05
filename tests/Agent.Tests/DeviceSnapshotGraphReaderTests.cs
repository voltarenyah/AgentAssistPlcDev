using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Contracts.Engineering;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// ADR-0011 Phase 2: the device read path serves the graph. These tests prove the values the page
/// shows are the values the crawl-based read produced (AC-002), that the read needs no exported file
/// once the device is projected (AC-001), that a device with no projection is projected on demand, and
/// that the picker's instance-DB exclusion survives (AC-008).
/// </summary>
public sealed class DeviceSnapshotGraphReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"device-snapshot-graph-{Guid.NewGuid():N}");

    [Fact]
    public void ServesTheSameSnapshotAsTheCrawlAndNeedsNoExportFileAfterIngest()
    {
        var fixture = new GraphFixture(_root);
        fixture.WriteDefaultManifest();
        fixture.WriteSource("Blocks/Area/Main [OB1].xml", BlockXml("SW.Blocks.OB", "Main", 1, "LAD"));
        fixture.WriteSource("DB/00_Common_Part/PC_Clock [DB2].xml", BlockXml("SW.Blocks.InstanceDB", "PC_Clock", 2, "DB"));
        fixture.WriteSource("DB/Global [DB5].xml", BlockXml("SW.Blocks.GlobalDB", "Global", 5, "DB"));
        File.WriteAllBytes(fixture.Context.KnowledgeDbPath, [1]);

        // The read the routes used before the change, for the same device and the same files.
        var crawl = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        using var store = new EngineeringGraphStore(_root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var fromGraph = new DeviceSnapshotGraphReader(graph).Read(fixture.Context, fixture.Metadata);

        AssertJsonEqual(JsonSerializer.SerializeToNode(crawl, WebOptions), JsonSerializer.SerializeToNode(fromGraph, WebOptions));
        // The values AC-002 names explicitly, so a passing comparison can never be an empty-versus-empty
        // coincidence.
        Assert.Equal(3, fromGraph.Blocks.Count);
        Assert.Equal(3, fromGraph.SourceObjectCount);
        Assert.Equal(5, fromGraph.SourceObjects.Count);
        Assert.Empty(fromGraph.Diagnostics);
        Assert.Equal("current", fromGraph.Knowledge.State);
        Assert.Equal("2026-07-29T08:00:00Z", fromGraph.Knowledge.UpdatedAt);
        var main = Assert.Single(fromGraph.SourceObjects, item => item.Id == "ob-1");
        Assert.True(main.IsKnowHowProtected);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero), main.ModifiedDate);
        Assert.Equal(ManagedSourceEvidenceKind.StandardBlock, main.EvidenceKind);
        Assert.Equal("AAAA1111", main.FingerprintComponents!["Code"]);
        var instanceDb = Assert.Single(fromGraph.SourceObjects, item => item.Id == "db-1");
        Assert.Equal(ManagedSourceEvidenceKind.InstanceDb, instanceDb.EvidenceKind);
        Assert.NotNull(fromGraph.Device);
        Assert.True(fromGraph.Device!.IsSafetyDevice);
        Assert.Equal("Station_1", fromGraph.Device.DeviceName);
        // A block keeps the crawl's own id and its always-false modified flag.
        Assert.All(fromGraph.Blocks, block => Assert.StartsWith("source:", block.Id, StringComparison.Ordinal));
        Assert.All(fromGraph.Blocks, block => Assert.False(block.Modified));

        // The ingest is done: the export files are now useless, and the read must not notice. The
        // manifest is deleted as well, so a read that fell back to disk could not produce this answer.
        fixture.CorruptExportFiles();

        var afterCorruption = new DeviceSnapshotGraphReader(graph).Read(fixture.Context, fixture.Metadata);
        AssertJsonEqual(
            JsonSerializer.SerializeToNode(fromGraph, WebOptions),
            JsonSerializer.SerializeToNode(afterCorruption, WebOptions));
        Assert.Equal(3, afterCorruption.Blocks.Count);
        Assert.Equal(5, afterCorruption.SourceObjects.Count);
    }

    [Fact]
    public void ProjectsADeviceOnDemandAndServesThePickerAndPageCountsFromOneProjection()
    {
        var fixture = new GraphFixture(_root);
        fixture.WriteManifestWithDeviceSection(
            Component("ob-1", "Main", "OB", "Blocks/Area/Main [OB1].xml", 1, "LAD", "Area/Main", "HASH-OB", "OB"),
            Component("db-1", "PC_Clock", "DB", "DB/PC_Clock [DB2].xml", 2, "DB", "PC_Clock", "HASH-DB", "InstanceDB"),
            Component("tags-1", "Inputs", "Tags", "Tags/LineA/Inputs.xml", null, null, "LineA/Inputs", "HASH-TAGS", null));

        using var store = new EngineeringGraphStore(_root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var reader = new DeviceSnapshotGraphReader(graph);
        // A database with no projection at all behaves like a projected one: the first read writes it.
        Assert.Null(graph.GetEntity(GraphEntityKind.Device, "plc-1"));

        var snapshot = reader.Read(fixture.Context, fixture.Metadata);

        Assert.NotNull(graph.GetEntity(GraphEntityKind.Device, "plc-1"));
        // The page counts block-category objects only (Blocks/ + DB/); the picker lists the comparable
        // manifest objects, so the instance DB is listed by the device and excluded from the picker.
        Assert.Equal(2, snapshot.SourceObjectCount);
        Assert.Equal(2, snapshot.Blocks.Count);
        Assert.Equal(3, snapshot.SourceObjects.Count);
        var picker = reader.ReadSourceObjects(fixture.Context, fixture.Metadata, comparableOnly: true);
        Assert.Equal(2, picker.Count);
        Assert.DoesNotContain(picker, item => item.Id == "db-1");
        Assert.Contains(picker, item => item.Id == "ob-1");
        Assert.Contains(picker, item => item.Id == "tags-1");
        // The same projection serves the two other shapes the routes ask for.
        Assert.Equal(
            snapshot.Blocks.Select(block => block.Id),
            reader.ReadBlocks(fixture.Context, fixture.Metadata).Select(block => block.Id));
        Assert.Equal(
            snapshot.SourceObjects.Select(item => item.Id),
            reader.ReadSourceObjects(fixture.Context, fixture.Metadata).Select(item => item.Id));
        // The graph-entity route's fallback resolves a listed object without reading the manifest.
        Assert.True(reader.TryGetListedSourceObject(fixture.Context, fixture.Metadata, "ob-1", out var path));
        Assert.Equal("Blocks/Area/Main [OB1].xml", path);
        Assert.False(reader.TryGetListedSourceObject(fixture.Context, fixture.Metadata, "not-listed", out _));
    }

    [Fact]
    public void ServesBlocksFromTheGraphWhenTheCrawlWasTheIngest()
    {
        var fixture = new GraphFixture(_root);
        fixture.WriteSource("Blocks/Area/Main [OB1].xml", BlockXml("SW.Blocks.OB", "Main", 1, "LAD"));
        fixture.WriteSource("DB/Area/Data [DB4].xml", BlockXml("SW.Blocks.InstanceDB", "Data", 4, "DB"));

        using var store = new EngineeringGraphStore(_root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var result = new EngineeringGraphProjectionService(graph).ProjectDevice(fixture.Context, fixture.Metadata);

        // The crawl cannot classify an instance DB, so the fallback marks it unclassified instead of
        // claiming it as a standard block (the picker's exclusion rule keeps working), and the page
        // still counts it as a block-category object.
        Assert.True(result.UsedCrawlFallback);
        Assert.Equal(2, result.BlockCount);
        Assert.Empty(result.StoredDiagnostics);
        var reader = new DeviceSnapshotGraphReader(graph);
        var snapshot = reader.Read(fixture.Context, fixture.Metadata);
        Assert.Equal(2, snapshot.SourceObjectCount);
        Assert.Null(Assert.Single(snapshot.SourceObjects, item => item.Id == "source:DB/Area/Data [DB4].xml").EvidenceKind);
        // The crawl cannot tell an instance DB from a global DB, so a DB-category object stays
        // unclassified and the picker lists it exactly as it did before the projection existed. The
        // manifest case (ProjectsADeviceOnDemandAndServesThePickerAndPageCountsFromOneProjection) is
        // where the instance-DB exclusion applies, and it is unchanged there (AC-008).
        Assert.Equal(2, reader.ReadSourceObjects(fixture.Context, fixture.Metadata, comparableOnly: true).Count);
    }

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Field-for-field comparison that names the first path that differs; object property
    /// order is not part of the contract (the fingerprint map is ordered deliberately on storage).</summary>
    private static void AssertJsonEqual(JsonNode? expected, JsonNode? actual, string path = "$")
    {
        if (expected is null || actual is null)
        {
            Assert.True(
                JsonNode.DeepEquals(expected, actual),
                $"{path}: expected {expected?.ToJsonString() ?? "null"}, got {actual?.ToJsonString() ?? "null"}");
            return;
        }

        if (expected is JsonObject expectedObject && actual is JsonObject actualObject)
        {
            foreach (var name in expectedObject.Select(pair => pair.Key)
                         .Union(actualObject.Select(pair => pair.Key), StringComparer.Ordinal))
            {
                AssertJsonEqual(expectedObject[name], actualObject[name], $"{path}.{name}");
            }

            return;
        }

        if (expected is JsonArray expectedArray && actual is JsonArray actualArray)
        {
            Assert.Equal(expectedArray.Count, actualArray.Count);
            for (var index = 0; index < expectedArray.Count; index++)
                AssertJsonEqual(expectedArray[index], actualArray[index], $"{path}[{index}]");
            return;
        }

        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"{path}: expected {expected.ToJsonString()}, got {actual.ToJsonString()}");
    }

    private static object Component(
        string id,
        string name,
        string category,
        string exportedFile,
        int? number,
        string? language,
        string? sourcePath,
        string? contentHash,
        string? siemensTypeName,
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

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class GraphFixture
    {
        public GraphFixture(string root)
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

        /// <summary>The five-component export the parity test compares: two block categories, an
        /// instance DB, a tag table and a UDT, with the manifest-only facts a page shows.</summary>
        public void WriteDefaultManifest() => WriteManifestWithDeviceSection(
            Component("ob-1", "Main", "OB", "Blocks/Area/Main [OB1].xml", 1, "LAD", "Area/Main", "HASH-OB", "OB",
                isKnowHowProtected: true, modifiedDate: "2026-07-20T10:00:00.0000000+00:00",
                fingerprints: new Dictionary<string, string> { ["Interface"] = "BBBB2222", ["Code"] = "AAAA1111" }),
            Component("db-1", "PC_Clock", "DB", "DB/00_Common_Part/PC_Clock [DB2].xml", 2, "DB",
                "00_Common_Part/PC_Clock", "HASH-DB", "InstanceDB"),
            Component("gdb-1", "Global", "DB", "DB/Global [DB5].xml", 5, "DB", "Global", "HASH-GDB", "GlobalDB"),
            Component("tags-1", "Inputs", "Tags", "Tags/LineA/Inputs.xml", null, null, "LineA/Inputs", "HASH-TAGS", null),
            Component("udt-1", "Motor", "UDT", "UDT/Models/Motor.xml", null, null, "Models/Motor", "HASH-UDT", null));

        public void WriteManifestWithDeviceSection(params object[] components) =>
            File.WriteAllText(
                Path.Combine(Context.SourceRoot, "metadata.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    device = new
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
                    },
                    components,
                }));

        public void WriteSource(string relativePath, string contents)
        {
            var path = WorkbenchPaths.ResolveRelative(Context.SourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        /// <summary>Everything the ingest could have read, made useless: the XML files are overwritten
        /// with content that could not parse into the values served, and the manifest is deleted.</summary>
        public void CorruptExportFiles()
        {
            foreach (var path in Directory.EnumerateFiles(Context.SourceRoot, "*.xml", SearchOption.AllDirectories))
                File.WriteAllText(path, "<not-a-siemens-document/>");
            var manifestPath = Path.Combine(Context.SourceRoot, "metadata.json");
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
        }
    }
}
