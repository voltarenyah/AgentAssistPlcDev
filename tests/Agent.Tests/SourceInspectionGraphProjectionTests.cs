using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// ADR-0011 Phase 5 (AC-001, AC-002): the source inspector's per-object parsed content is a projected
/// fact. The inspect read returns exactly what <see cref="SourceObjectInspectorReader"/> returned for
/// the same file — after the exported XML has been corrupted — and the device read never loads the
/// payload. The inspector itself stays as the ingest.
/// </summary>
public sealed class SourceInspectionGraphProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"source-inspection-graph-{Guid.NewGuid():N}");

    [Fact]
    public void ServesTheSameInspectionPayloadAfterTheXmlIsCorrupted()
    {
        var fixture = new InspectionFixture(_root);
        fixture.WriteManifest(
            Component("ob-1", "OB", "Blocks/Area/Main [OB1].xml", "HASH-OB"),
            Component("tags-1", "Tags", "Tags/LineA/Signals.xml", "HASH-TAGS"));
        fixture.WriteSource("Blocks/Area/Main [OB1].xml", BlockXml);
        fixture.WriteSource("Tags/LineA/Signals.xml", TagsXml);

        // The payload the route produced before the change, for the same file.
        var expected = new SourceObjectInspectorReader().Read(
            fixture.Context, "Blocks/Area/Main [OB1].xml", new DeviceSourceResolver(_ => { }));

        using var store = new EngineeringGraphStore(_root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var reader = new DeviceSnapshotGraphReader(graph);

        var actual = reader.ReadInspection(fixture.Context, fixture.Metadata, "Blocks/Area/Main [OB1].xml");

        AssertJsonEqual(expected, actual);
        // The values AC-002 names explicitly, so a passing comparison can never be an empty coincidence.
        Assert.Equal("Blocks", actual.Category);
        Assert.Equal("Main", actual.Name);
        Assert.Single(actual.Interfaces);
        var network = Assert.Single(actual.Networks);
        Assert.NotNull(network.Ladder);
        Assert.Equal(5, network.Ladder!.Wires.Count);
        Assert.Equal("Main", reader.ReadInspection(fixture.Context, fixture.Metadata, "Blocks\\Area\\Main [OB1].xml").Name);

        // The ingest is done: every exported XML file is replaced by content that could not parse into
        // those values, and the read must not notice. A read that opened the file would fail here.
        fixture.CorruptExportFiles();

        var afterCorruption = reader.ReadInspection(fixture.Context, fixture.Metadata, "Blocks/Area/Main [OB1].xml");
        AssertJsonEqual(expected, afterCorruption);
        var tagTable = reader.ReadInspection(fixture.Context, fixture.Metadata, "Tags/LineA/Signals.xml");
        Assert.Equal("Tags", tagTable.Category);
        Assert.Equal("MotorRun", Assert.Single(Assert.Single(tagTable.Tables).Rows)["Name"]);
    }

    [Fact]
    public void ServesTheInspectorsOwnErrorForAnObjectItCannotInspect()
    {
        var fixture = new InspectionFixture(_root);
        fixture.WriteManifest(
            Component("ob-1", "OB", "Blocks/Area/Unsupported.xml", "HASH-A"),
            Component("ob-2", "OB", "Blocks/Area/Malformed.xml", "HASH-B"));
        fixture.WriteSource("Blocks/Area/Unsupported.xml", "<Document><NotASiemensObject /></Document>");
        fixture.WriteSource("Blocks/Area/Malformed.xml", "<Document><SW.Blocks.OB>");

        using var store = new EngineeringGraphStore(_root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var reader = new DeviceSnapshotGraphReader(graph);

        var unsupported = Assert.Throws<SourceInspectionException>(
            () => reader.ReadInspection(fixture.Context, fixture.Metadata, "Blocks/Area/Unsupported.xml"));
        Assert.Equal("SOURCE_INSPECTION_UNSUPPORTED", unsupported.Code);
        var malformed = Assert.Throws<SourceInspectionException>(
            () => reader.ReadInspection(fixture.Context, fixture.Metadata, "Blocks/Area/Malformed.xml"));
        Assert.Equal("SOURCE_INSPECTION_XML_INVALID", malformed.Code);

        // The stored error survives the file being replaced: the route answers the same way.
        fixture.CorruptExportFiles();
        Assert.Equal(
            "SOURCE_INSPECTION_UNSUPPORTED",
            Assert.Throws<SourceInspectionException>(
                () => reader.ReadInspection(fixture.Context, fixture.Metadata, "Blocks/Area/Unsupported.xml")).Code);

        // A path the projection does not list is a missing object, never a silent empty payload.
        Assert.Throws<FileNotFoundException>(
            () => reader.ReadInspection(fixture.Context, fixture.Metadata, "Blocks/Area/Unknown.xml"));
        // The caller's path validation is unchanged.
        Assert.Throws<WorkbenchPathException>(
            () => reader.ReadInspection(fixture.Context, fixture.Metadata, "../outside.xml"));
    }

    [Fact]
    public void ReusesStoredContentSoAProjectionThatRunsAgainParsesNoXml()
    {
        var fixture = new InspectionFixture(_root);
        fixture.WriteManifest(Component("ob-1", "OB", "Blocks/Area/Main [OB1].xml", "HASH-OB"));
        fixture.WriteSource("Blocks/Area/Main [OB1].xml", BlockXml);

        using var store = new EngineeringGraphStore(_root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var projection = new EngineeringGraphProjectionService(graph);
        var first = projection.ProjectDevice(fixture.Context, fixture.Metadata);
        var expected = new SourceObjectInspectorReader().Read(
            fixture.Context, "Blocks/Area/Main [OB1].xml", new DeviceSourceResolver(_ => { }));
        Assert.True(first.PropertyRowsWritten > 0);

        // The XML is gone, so a projection that re-read it would record an error instead of the payload.
        fixture.CorruptExportFiles();
        var second = projection.ProjectDevice(fixture.Context, fixture.Metadata);

        Assert.Equal(0, second.PropertyRowsWritten);
        AssertJsonEqual(
            expected,
            new DeviceSnapshotGraphReader(graph).ReadInspection(fixture.Context, fixture.Metadata, "Blocks/Area/Main [OB1].xml"));
    }

    [Fact]
    public void ReprojectsAStoredProjectionThatPredatesTheInspectionFacts()
    {
        var fixture = new InspectionFixture(_root);
        fixture.WriteManifest(Component("ob-1", "OB", "Blocks/Area/Main [OB1].xml", "HASH-OB"));
        fixture.WriteSource("Blocks/Area/Main [OB1].xml", BlockXml);

        using var store = new EngineeringGraphStore(_root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var reader = new DeviceSnapshotGraphReader(graph);
        reader.Read(fixture.Context, fixture.Metadata);
        Assert.Equal(
            ProjectionBoundaryReason.None,
            reader.EnsureProjectionCurrent(fixture.Context, fixture.Metadata).Reason);

        // A database projected before this phase carries no fact-set version, exactly as one projected
        // by an earlier build would not.
        Execute(store, $"DELETE FROM graph_entity_properties WHERE entity_kind = 'source_object' AND name LIKE 'inspection.%';");
        Execute(store, $"DELETE FROM graph_entity_properties WHERE entity_kind = 'device' AND name = '{DevicePropertyNames.ProjectionFactsVersion}';");

        var boundary = reader.EnsureProjectionCurrent(fixture.Context, fixture.Metadata);

        Assert.Equal(ProjectionBoundaryReason.Outdated, boundary.Reason);
        Assert.True(boundary.Reprojected);
        Assert.Equal("Main", reader.ReadInspection(fixture.Context, fixture.Metadata, "Blocks/Area/Main [OB1].xml").Name);
    }

    [Fact]
    public void KeepsTheParsedContentOutOfTheDeviceReadItIsNotNeededFor()
    {
        var fixture = new InspectionFixture(_root);
        fixture.WriteManifest(Component("ob-1", "OB", "Blocks/Area/Main [OB1].xml", "HASH-OB"));
        fixture.WriteSource("Blocks/Area/Main [OB1].xml", BlockXml);

        using var store = new EngineeringGraphStore(_root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var reader = new DeviceSnapshotGraphReader(graph);
        var snapshot = reader.Read(fixture.Context, fixture.Metadata);

        // The device page's own facts are unchanged and the payload is not part of them.
        Assert.Single(snapshot.SourceObjects);
        Assert.Single(snapshot.Blocks);
        Assert.DoesNotContain(
            graph.GetDeviceProperties(fixture.Context.DeviceId).Values.SelectMany(properties => properties),
            property => property.Name.StartsWith(SourceObjectInspectionPropertyNames.Prefix, StringComparison.Ordinal));
        Assert.DoesNotContain(
            graph.GetInspectionFacts(fixture.Context.DeviceId).Values.SelectMany(properties => properties),
            property => property.Name == SourceObjectInspectionPropertyNames.Payload);
        // The payload is stored, and readable for the one node that needs it.
        Assert.Contains(
            graph.GetProperties(GraphEntityKind.SourceObject, "plc-1:ob-1"),
            property => property.Name == SourceObjectInspectionPropertyNames.Payload && property.Json is not null);
    }

    private static void Execute(EngineeringGraphStore store, string sql)
    {
        using var command = store.Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void AssertJsonEqual<T>(T expected, T actual) =>
        Assert.True(
            JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                JsonSerializer.SerializeToNode(actual, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            $"expected {JsonSerializer.Serialize(expected)}, got {JsonSerializer.Serialize(actual)}");

    private static object Component(string id, string category, string exportedFile, string contentHash) =>
        new
        {
            id,
            name = Path.GetFileNameWithoutExtension(exportedFile),
            sourcePath = Path.GetFileNameWithoutExtension(exportedFile),
            category,
            status = "Exported",
            exportedFile,
            number = (int?)null,
            programmingLanguage = (string?)null,
            contentHash,
            siemensTypeName = (string?)null,
            isKnowHowProtected = (bool?)null,
            modifiedDate = (string?)null,
            fingerprints = (Dictionary<string, string>?)null,
        };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>One device export root shaped like the workbench's own layout.</summary>
    private sealed class InspectionFixture
    {
        public InspectionFixture(string root)
        {
            var worktreeRoot = Path.Combine(root, "worktrees", "master");
            var deviceRoot = Path.Combine(worktreeRoot, "devices", "plc-1");
            var sourceRoot = Path.Combine(deviceRoot, "source");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(Path.Combine(deviceRoot, "staging"));
            Context = new DeviceContext(
                "wb-1", "wt-1", "plc-1", root, worktreeRoot, deviceRoot, sourceRoot,
                Path.Combine(deviceRoot, "staging"), Path.Combine(deviceRoot, "plc-knowledge.db"));
            Metadata = new DeviceMetadata(
                WorkbenchSchema.CurrentVersion, "plc-1", "wt-1", "PLC 1", "engineering-plc-1",
                null, null, null,
                new KnowledgeState(false, new Dictionary<string, string>(), "2026-07-29T08:00:00Z", false),
                []);
        }

        public DeviceContext Context { get; }
        public DeviceMetadata Metadata { get; }

        public void WriteManifest(params object[] components) =>
            File.WriteAllText(
                Path.Combine(Context.SourceRoot, "metadata.json"),
                JsonSerializer.Serialize(new { schemaVersion = "1.0", device = new { plcName = "PLC_1" }, components }));

        public void WriteSource(string relativePath, string contents)
        {
            var path = WorkbenchPaths.ResolveRelative(Context.SourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        /// <summary>Every exported file made useless, without touching the manifest — so the device's
        /// digest still agrees and no re-projection is triggered.</summary>
        public void CorruptExportFiles()
        {
            foreach (var path in Directory.EnumerateFiles(Context.SourceRoot, "*.xml", SearchOption.AllDirectories))
                File.WriteAllText(path, "<not-a-siemens-document/>");
        }
    }

    private const string BlockXml = """
        <Document><SW.Blocks.FC ID="block"><AttributeList><Name>Main</Name></AttributeList>
          <Interface><Sections><Section Name="Input"><Member Name="Enable" Datatype="Bool" DefaultValue="false" /></Section></Sections></Interface>
          <SW.Blocks.CompileUnit ID="network-1"><AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>
            <FlgNet><Parts><Access UId="1"><Symbol><Component Name="Enable" /></Symbol></Access><Access UId="2"><Symbol><Component Name="Delay" /></Symbol></Access><Part UId="3" Name="Contact" /><Part UId="4" Name="TON"><Instance UId="5"><Component Name="TimerDb" /></Instance></Part><Part UId="6" Name="Sr" /></Parts>
            <Wires>
              <Wire><Powerrail /><NameCon UId="3" Name="in" /></Wire>
              <Wire><IdentCon UId="1" /><NameCon UId="3" Name="operand" /></Wire>
              <Wire><NameCon UId="3" Name="out" /><NameCon UId="4" Name="IN" /></Wire>
              <Wire><IdentCon UId="2" /><NameCon UId="4" Name="PT" /></Wire>
              <Wire><NameCon UId="4" Name="Q" /><NameCon UId="6" Name="s" /></Wire>
            </Wires></FlgNet>
          </SW.Blocks.CompileUnit>
        </SW.Blocks.FC></Document>
        """;

    private const string TagsXml = """
        <Document><SW.Tags.PlcTagTable><AttributeList><Name>Signals</Name></AttributeList><ObjectList>
          <SW.Tags.PlcTag><AttributeList><Name>MotorRun</Name><DataTypeName>Bool</DataTypeName><LogicalAddress>%Q0.0</LogicalAddress></AttributeList></SW.Tags.PlcTag>
        </ObjectList></SW.Tags.PlcTagTable></Document>
        """;
}
