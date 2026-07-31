using Agent.Workbench;
using System.Text.Json;
using Xunit;

namespace Agent.Tests;

public sealed class DeviceSnapshotReaderTests
{
    [Theory]
    [InlineData(false, false, false, "missing")]
    [InlineData(true, true, false, "stale")]
    [InlineData(true, false, true, "stale")]
    [InlineData(true, false, false, "current")]
    public void KnowledgeStateUsesDatabaseExistenceAndPersistedFlags(
        bool databaseExists,
        bool stale,
        bool baselineStale,
        string expected)
    {
        using var fixture = SnapshotFixture.Create(stale, baselineStale);
        if (databaseExists)
            File.WriteAllBytes(fixture.Context.KnowledgeDbPath, [1]);

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Equal(expected, snapshot.Knowledge.State);
        Assert.Equal(fixture.Metadata.Knowledge.UpdatedAt, snapshot.Knowledge.UpdatedAt);
    }

    [Fact]
    public void ReadBuildsOfflineBlockIndexFromExportManifest()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteManifest(
            Component("ob", "Main", "OB", "Blocks/Main [OB1].xml", 1, "LAD", "Area/Main"),
            Component("fb", "Helper", "FB", "Blocks/Helper [FB2].xml", 2, "SCL", "Area/Helper"),
            Component("db", "Data", "DB", "DB/Data [DB4].xml", 4, null, "Area/Data"),
            Component("tags", "Tags", "Tags", "Tags/Tags.xml", null, null, "Tags"),
            Component("udt", "Recipe", "UDT", "UDT/Recipe.xml", null, null, "Recipe"));

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Collection(
            snapshot.Blocks,
            block =>
            {
                Assert.Equal("Data", block.Name);
                Assert.Equal("DB", block.BlockType);
                Assert.Equal(4, block.Number);
                Assert.Equal("Area", block.GroupPath);
                Assert.Equal("DB/Data [DB4].xml", block.RelativePath);
                Assert.False(block.Modified);
            },
            block =>
            {
                Assert.Equal("Helper", block.Name);
                Assert.Equal("FB", block.BlockType);
                Assert.Equal("SCL", block.ProgrammingLanguage);
            },
            block =>
            {
                Assert.Equal("Main", block.Name);
                Assert.Equal("OB", block.BlockType);
                Assert.Equal(1, block.Number);
                Assert.Equal("LAD", block.ProgrammingLanguage);
            });
    }

    [Fact]
    public void ReadScalesToLargeManifestsWithoutRepeatedPathValidation()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteManifest(Enumerable.Range(1, 2000)
            .Select(i => Component($"fb{i}", $"Block{i}", "FB", $"Blocks/Block{i} [FB{i}].xml", i, "LAD", "Area"))
            .ToArray());

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        Assert.Equal(2000, snapshot.Blocks.Count);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(1),
            $"Snapshot read over 600 components took {elapsed.TotalMilliseconds:F0} ms; expected sub-second.");
    }

    [Fact]
    public void ReadSurfacesManifestDeviceSection()
    {
        using var fixture = SnapshotFixture.Create();
        File.WriteAllText(
            Path.Combine(fixture.Context.ExportedSourceRoot, "metadata.json"),
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
                },
                components = Array.Empty<object>(),
            }));

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.NotNull(snapshot.Device);
        Assert.Equal("PLC_1", snapshot.Device!.PlcName);
        Assert.Equal("Station_1", snapshot.Device.DeviceName);
        Assert.Equal("OrderNumber:6ES7515-2AM02-0AB0/V2.9", snapshot.Device.TypeIdentifier);
        Assert.Equal("TestPLCExportDemo", snapshot.Device.ProjectName);
        Assert.Equal("Ansel", snapshot.Device.ProjectAuthor);
        Assert.Equal("demo project", snapshot.Device.ProjectComment);
        Assert.Equal("V17", snapshot.Device.ProjectVersion);
        Assert.Null(snapshot.Device.ProjectCopyright);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero), snapshot.Device.ProjectCreationTime);
        Assert.Equal(new DateTimeOffset(2026, 7, 30, 9, 30, 0, TimeSpan.Zero), snapshot.Device.ProjectLastModified);
        Assert.Equal("Ansel", snapshot.Device.ProjectLastModifiedBy);
    }

    [Fact]
    public void ReadWithoutDeviceSection_ReturnsNullDevice()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteManifest(Component("ob", "Main", "OB", "Blocks/Main [OB1].xml", 1, "LAD", "Area/Main"));

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Null(snapshot.Device);
    }

    [Fact]
    public void ReadMergesModifiedAndOverlayOnlyBlocks()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteManifest(
            Component("ob", "Main", "OB", "Blocks/Main [OB1].xml", 1, "LAD", "Area/Main"));
        fixture.WriteOverlay(
            "Blocks/Main [OB1].xml",
            BlockXml("SW.Blocks.OB", "Main", 1, "LAD"));
        fixture.WriteOverlay(
            "Blocks/Local [FC7].xml",
            BlockXml("SW.Blocks.FC", "Local", 7, "SCL"));

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Equal(2, snapshot.OverlayCount);
        Assert.Equal(2, snapshot.Blocks.Count);
        Assert.True(snapshot.Blocks.Single(block => block.Name == "Main").Modified);
        var local = snapshot.Blocks.Single(block => block.Name == "Local");
        Assert.True(local.Modified);
        Assert.Equal("FC", local.BlockType);
        Assert.Equal(7, local.Number);
        Assert.Equal("SCL", local.ProgrammingLanguage);
        Assert.Equal("Blocks/Local [FC7].xml", local.RelativePath);
    }

    [Fact]
    public void ReadUsesMatchingOverlayMetadataAsTheEffectiveBlock()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteManifest(
            Component("ob", "Old Main", "OB", "Blocks/Area/Main [OB1].xml", 1, "LAD", "Old/Old Main"));
        fixture.WriteOverlay(
            "Blocks/Area/Main [OB1].xml",
            BlockXml("SW.Blocks.OB", "New Main", 9, "SCL"));

        var block = Assert.Single(new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata).Blocks);

        Assert.Equal("New Main", block.Name);
        Assert.Equal(9, block.Number);
        Assert.Equal("SCL", block.ProgrammingLanguage);
        Assert.Equal("Area", block.GroupPath);
        Assert.True(block.Modified);
    }

    [Fact]
    public void ReadReportsMalformedMatchingOverlayAndRetainsUnmodifiedBaselineMetadata()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteManifest(
            Component("ob", "Main", "OB", "Blocks/Main [OB1].xml", 1, "LAD", "Main"));
        fixture.WriteOverlay("Blocks/Main [OB1].xml", "<Document><SW.Blocks.OB>");

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        var block = Assert.Single(snapshot.Blocks);
        Assert.Equal("Main", block.Name);
        Assert.Equal(1, block.Number);
        Assert.Equal("LAD", block.ProgrammingLanguage);
        Assert.False(block.Modified);
        Assert.Contains(snapshot.Diagnostics, message =>
            message.Contains("Blocks/Main [OB1].xml", StringComparison.Ordinal)
            && message.Contains("supported Siemens PLC block", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadReportsAmbiguousMatchingOverlayAndRetainsUnmodifiedBaselineMetadata()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteManifest(
            Component("ob", "Main", "OB", "Blocks/Main [OB1].xml", 1, "LAD", "Main"));
        fixture.WriteOverlay(
            "Blocks/Main [OB1].xml",
            """
            <Document>
              <SW.Blocks.OB><AttributeList><Name>One</Name></AttributeList></SW.Blocks.OB>
              <SW.Blocks.FC><AttributeList><Name>Two</Name></AttributeList></SW.Blocks.FC>
            </Document>
            """);

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        var block = Assert.Single(snapshot.Blocks);
        Assert.Equal("Main", block.Name);
        Assert.False(block.Modified);
        Assert.Contains(snapshot.Diagnostics, message =>
            message.Contains("Blocks/Main [OB1].xml", StringComparison.Ordinal)
            && message.Contains("exactly one direct", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadReturnsDiagnosticWhenManifestIsMissing()
    {
        using var fixture = SnapshotFixture.Create();

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Empty(snapshot.Blocks);
        Assert.Contains(snapshot.Diagnostics, message => message.StartsWith(
            "Export manifest is missing:", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadReturnsDiagnosticWhenManifestIsMalformed()
    {
        using var fixture = SnapshotFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Context.ExportedSourceRoot, "metadata.json"), "{ broken");

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Empty(snapshot.Blocks);
        Assert.Contains(snapshot.Diagnostics, message => message.StartsWith(
            "Export manifest is invalid JSON:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"manifest\"")]
    public void ReadReturnsDiagnosticWhenManifestRootIsNotAnObject(string json)
    {
        using var fixture = SnapshotFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Context.ExportedSourceRoot, "metadata.json"), json);

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Empty(snapshot.Blocks);
        Assert.Contains(snapshot.Diagnostics, message => message.Contains(
            "root must be a JSON object", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadSkipsNonObjectManifestComponentsWithDiagnostic()
    {
        using var fixture = SnapshotFixture.Create();
        File.WriteAllText(
            Path.Combine(fixture.Context.ExportedSourceRoot, "metadata.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                components = new object[]
                {
                    "invalid",
                    Component("ob", "Main", "OB", "Blocks/Main [OB1].xml", 1, "LAD", "Main"),
                },
            }));

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Single(snapshot.Blocks);
        Assert.Contains(snapshot.Diagnostics, message => message.Contains(
            "component at index 0 must be a JSON object", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadReportsUnsupportedOverlayXmlWithoutDiscardingManifestBlocks()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteManifest(
            Component("ob", "Main", "OB", "Blocks/Main [OB1].xml", 1, "LAD", "Main"));
        fixture.WriteOverlay("Tags/NotABlock.xml", "<Document><SW.Tags.PlcTagTable /></Document>");

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Single(snapshot.Blocks);
        Assert.Equal(1, snapshot.OverlayCount);
        Assert.Contains(snapshot.Diagnostics, message =>
            message.Contains("Tags/NotABlock.xml", StringComparison.Ordinal)
            && message.Contains("supported Siemens PLC block", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadRejectsOverlayWithMultipleSupportedBlocks()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteManifest();
        fixture.WriteOverlay(
            "Blocks/Ambiguous.xml",
            """
            <Document>
              <SW.Blocks.OB><AttributeList><Name>One</Name></AttributeList></SW.Blocks.OB>
              <Payload>
                <SW.Blocks.FC><AttributeList><Name>Two</Name></AttributeList></SW.Blocks.FC>
              </Payload>
            </Document>
            """);

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Empty(snapshot.Blocks);
        Assert.Contains(snapshot.Diagnostics, message =>
            message.Contains("Blocks/Ambiguous.xml", StringComparison.Ordinal)
            && message.Contains("exactly one direct", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadRejectsSupportedBlockNestedUnderUnrelatedEnvelope()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteManifest();
        fixture.WriteOverlay(
            "Blocks/Nested.xml",
            """
            <Envelope>
              <Payload>
                <SW.Blocks.OB><AttributeList><Name>Nested</Name></AttributeList></SW.Blocks.OB>
              </Payload>
            </Envelope>
            """);

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Empty(snapshot.Blocks);
        Assert.Contains(snapshot.Diagnostics, message =>
            message.Contains("Blocks/Nested.xml", StringComparison.Ordinal)
            && message.Contains("Document root", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadReportsManifestPathsThatEscapeTheExportRoot()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteManifest(
            Component("ob", "Main", "OB", "../Main.xml", 1, "LAD", "Main"));

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Empty(snapshot.Blocks);
        Assert.Contains(snapshot.Diagnostics, message =>
            message.Contains("invalid exportedFile '../Main.xml'", StringComparison.Ordinal));
    }

    private static object Component(
        string id,
        string name,
        string category,
        string exportedFile,
        int? number,
        string? language,
        string sourcePath) =>
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

    private sealed class SnapshotFixture : IDisposable
    {
        private SnapshotFixture(string root, DeviceContext context, DeviceMetadata metadata)
        {
            Root = root;
            Context = context;
            Metadata = metadata;
        }

        public string Root { get; }
        public DeviceContext Context { get; }
        public DeviceMetadata Metadata { get; }

        public static SnapshotFixture Create(bool stale = false, bool baselineStale = false)
        {
            var root = Path.Combine(Path.GetTempPath(), $"device-snapshot-tests-{Guid.NewGuid():N}");
            var worktreeRoot = Path.Combine(root, "worktrees", "main");
            var deviceRoot = Path.Combine(worktreeRoot, "devices", "plc-1");
            var exportedRoot = Path.Combine(deviceRoot, "exported-source");
            var modifiedRoot = Path.Combine(deviceRoot, "modified-source");
            var stagingRoot = Path.Combine(deviceRoot, "staging");
            Directory.CreateDirectory(exportedRoot);
            Directory.CreateDirectory(modifiedRoot);
            Directory.CreateDirectory(stagingRoot);

            var context = new DeviceContext(
                "wb-1",
                "wt-1",
                "plc-1",
                root,
                worktreeRoot,
                deviceRoot,
                exportedRoot,
                modifiedRoot,
                stagingRoot,
                Path.Combine(deviceRoot, "plc-knowledge.db"));
            var metadata = new DeviceMetadata(
                WorkbenchSchema.CurrentVersion,
                "plc-1",
                "wt-1",
                "PLC 1",
                "engineering-plc-1",
                null,
                null,
                null,
                new KnowledgeState(
                    stale,
                    new Dictionary<string, string>(),
                    "2026-07-29T08:00:00Z",
                    baselineStale),
                []);

            return new SnapshotFixture(root, context, metadata);
        }

        public void WriteManifest(params object[] components)
        {
            File.WriteAllText(
                Path.Combine(Context.ExportedSourceRoot, "metadata.json"),
                JsonSerializer.Serialize(new { schemaVersion = "1.0", components }));
        }

        public void WriteOverlay(string relativePath, string contents)
        {
            var path = WorkbenchPaths.ResolveRelative(Context.ModifiedSourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
