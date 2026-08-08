using Agent.Workbench;
using System.Text.Json;
using Xunit;

namespace Agent.Tests;

public sealed class DeviceSnapshotReaderTests
{
    [Fact]
    public void SnapshotContractExposesOnlyTheSingleSourceRootAndSourceObjectCount()
    {
        var properties = typeof(DeviceSnapshot).GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains("SourceRoot", properties);
        Assert.Contains("SourceObjectCount", properties);
        Assert.DoesNotContain("ExportedSourceRoot", properties);
        Assert.DoesNotContain("ModifiedSourceRoot", properties);
        Assert.DoesNotContain("OverlayCount", properties);
    }

    [Fact]
    public void ReadCrawlsSupportedSourceXmlWithoutMetadata()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteSource(
            "Blocks/Area/Main [OB1].xml",
            BlockXml("SW.Blocks.OB", "Main", 1, "LAD"));
        fixture.WriteSource(
            "DB/Recipes/Recipe [DB4].xml",
            BlockXml("SW.Blocks.GlobalDB", "Recipe", 4, "DB"));

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Equal(fixture.Context.SourceRoot, PropertyValue<string>(snapshot, "SourceRoot"));
        Assert.Equal(2, PropertyValue<int>(snapshot, "SourceObjectCount"));
        Assert.Collection(
            snapshot.Blocks,
            block =>
            {
                Assert.Equal("Recipe", block.Name);
                Assert.Equal("DB", block.BlockType);
                Assert.Equal(4, block.Number);
                Assert.Equal("DB", block.ProgrammingLanguage);
                Assert.Equal("Recipes", block.GroupPath);
                Assert.Equal("DB/Recipes/Recipe [DB4].xml", block.RelativePath);
                Assert.False(block.Modified);
            },
            block =>
            {
                Assert.Equal("Main", block.Name);
                Assert.Equal("OB", block.BlockType);
                Assert.Equal(1, block.Number);
                Assert.Equal("LAD", block.ProgrammingLanguage);
                Assert.Equal("Area", block.GroupPath);
                Assert.Equal("Blocks/Area/Main [OB1].xml", block.RelativePath);
                Assert.False(block.Modified);
            });
        Assert.DoesNotContain(snapshot.Diagnostics, diagnostic =>
            diagnostic.Contains("manifest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadUsesTheFilenameWhenOptionalXmlIdentityFieldsAreMissing()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteSource(
            "Blocks/Area/Fallback Name [FC17].xml",
            """
            <Document>
              <SW.Blocks.FC ID="0">
                <AttributeList><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList>
              </SW.Blocks.FC>
            </Document>
            """);

        var block = Assert.Single(new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata).Blocks);

        Assert.Equal("Fallback Name", block.Name);
        Assert.Equal("FC", block.BlockType);
        Assert.Equal(17, block.Number);
        Assert.Equal("SCL", block.ProgrammingLanguage);
        Assert.Equal("Area", block.GroupPath);
        Assert.False(block.Modified);
    }

    [Fact]
    public void ReadReportsMalformedXmlAndContinuesWithDeterministicOrdering()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteSource("Blocks/Zed [OB9].xml", BlockXml("SW.Blocks.OB", "Zed", 9, "LAD"));
        fixture.WriteSource("Blocks/Broken.xml", "<Document><SW.Blocks.FC>");
        fixture.WriteSource("Blocks/Alpha [OB2].xml", BlockXml("SW.Blocks.OB", "Alpha", 2, "LAD"));
        fixture.WriteSource("Blocks/Beta [FB3].xml", BlockXml("SW.Blocks.FB", "Beta", 3, "SCL"));

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Equal(
            ["FB:3:Beta", "OB:2:Alpha", "OB:9:Zed"],
            snapshot.Blocks.Select(block => $"{block.BlockType}:{block.Number}:{block.Name}"));
        Assert.Equal(3, PropertyValue<int>(snapshot, "SourceObjectCount"));
        Assert.Contains(snapshot.Diagnostics, diagnostic =>
            diagnostic.Contains("Blocks/Broken.xml", StringComparison.Ordinal)
            && diagnostic.Contains("malformed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadRejectsReparsePointPathsWithoutLeavingTheSourceRoot()
    {
        using var fixture = SnapshotFixture.Create();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(
            Path.Combine(outside, "Escaped [OB99].xml"),
            BlockXml("SW.Blocks.OB", "Escaped", 99, "LAD"));
        var link = Path.Combine(fixture.Context.SourceRoot, "Linked");
        CreateDirectoryLink(link, outside);

        try
        {
            var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

            Assert.Empty(snapshot.Blocks);
            Assert.Contains(snapshot.Diagnostics, diagnostic =>
                diagnostic.Contains("Linked", StringComparison.Ordinal)
                && diagnostic.Contains("reparse", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, PropertyValue<int>(snapshot, "SourceObjectCount"));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

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
    public void ReadBuildsOfflineBlockIndexFromSourceXml()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteSource("Blocks/Area/Main [OB1].xml", BlockXml("SW.Blocks.OB", "Main", 1, "LAD"));
        fixture.WriteSource("Blocks/Area/Helper [FB2].xml", BlockXml("SW.Blocks.FB", "Helper", 2, "SCL"));
        fixture.WriteSource("DB/Area/Data [DB4].xml", BlockXml("SW.Blocks.InstanceDB", "Data", 4, "DB"));

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Collection(
            snapshot.Blocks,
            block =>
            {
                Assert.Equal("Data", block.Name);
                Assert.Equal("DB", block.BlockType);
                Assert.Equal(4, block.Number);
                Assert.Equal("Area", block.GroupPath);
                Assert.Equal("DB/Area/Data [DB4].xml", block.RelativePath);
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
    public void ReadNeverMarksOrdinarySourceAsModified()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteSource("Blocks/Main [OB1].xml", BlockXml("SW.Blocks.OB", "Main", 1, "LAD"));

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Equal(1, snapshot.SourceObjectCount);
        Assert.False(Assert.Single(snapshot.Blocks).Modified);
    }

    [Fact]
    public void ReadScalesToLargeSourceTrees()
    {
        using var fixture = SnapshotFixture.Create();
        foreach (var number in Enumerable.Range(1, 600))
        {
            fixture.WriteSource(
                $"Blocks/Area/Block{number} [FB{number}].xml",
                BlockXml("SW.Blocks.FB", $"Block{number}", number, "LAD"));
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        Assert.Equal(600, snapshot.SourceObjectCount);
        Assert.True(
            elapsed < TimeSpan.FromSeconds(2),
            $"Snapshot read over 600 source objects took {elapsed.TotalMilliseconds:F0} ms; expected under two seconds.");
    }

    [Fact]
    public void ReadSurfacesManifestDeviceSection()
    {
        using var fixture = SnapshotFixture.Create();
        File.WriteAllText(
            Path.Combine(fixture.Context.SourceRoot, "metadata.json"),
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
        fixture.WriteManifest();

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Null(snapshot.Device);
    }

    [Fact]
    public void ReadUsesXmlIdentityInsteadOfManifestComponentIdentity()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteManifest(
            Component("ob", "Old Main", "OB", "Blocks/Area/Main [OB1].xml", 1, "LAD", "Old/Main"));
        fixture.WriteSource(
            "Blocks/Area/Main [OB1].xml",
            BlockXml("SW.Blocks.OB", "New Main", 9, "SCL"));

        var block = Assert.Single(new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata).Blocks);

        Assert.Equal("New Main", block.Name);
        Assert.Equal(9, block.Number);
        Assert.Equal("SCL", block.ProgrammingLanguage);
        Assert.Equal("Area", block.GroupPath);
        Assert.False(block.Modified);
    }

    [Fact]
    public void ReadMalformedMetadataDoesNotBlockSourceDiscovery()
    {
        using var fixture = SnapshotFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Context.SourceRoot, "metadata.json"), "{ broken");
        fixture.WriteSource("Blocks/Main [OB1].xml", BlockXml("SW.Blocks.OB", "Main", 1, "LAD"));

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Single(snapshot.Blocks);
        Assert.Null(snapshot.Device);
        Assert.DoesNotContain(snapshot.Diagnostics, message =>
            message.Contains("metadata.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReadSkipsNonBlockCategoriesSilentlyWithoutDiscardingSupportedBlocks()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteSource("Blocks/Main [OB1].xml", BlockXml("SW.Blocks.OB", "Main", 1, "LAD"));
        fixture.WriteSource("Tags/NotABlock.xml", "<Document><SW.Tags.PlcTagTable /></Document>");

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Single(snapshot.Blocks);
        // Tags/UDT exports are valid non-block categories: skipped without a diagnostic
        // (previously one spurious "malformed or unsupported" warning per file per snapshot).
        Assert.DoesNotContain(snapshot.Diagnostics, message =>
            message.Contains("Tags/NotABlock.xml", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadRejectsSourceXmlWithMultipleSupportedBlocks()
    {
        using var fixture = SnapshotFixture.Create();
        fixture.WriteSource(
            "Blocks/Ambiguous.xml",
            """
            <Document>
              <SW.Blocks.OB><AttributeList><Name>One</Name></AttributeList></SW.Blocks.OB>
              <SW.Blocks.FC><AttributeList><Name>Two</Name></AttributeList></SW.Blocks.FC>
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
        fixture.WriteSource(
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

    private static T PropertyValue<T>(DeviceSnapshot snapshot, string name) =>
        Assert.IsType<T>(typeof(DeviceSnapshot).GetProperty(name)?.GetValue(snapshot));

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var process = System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;

        process.WaitForExit();
        Assert.True(
            process.ExitCode == 0,
            $"Could not create test junction: {process.StandardError.ReadToEnd()}");
    }

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
            var sourceRoot = Path.Combine(deviceRoot, "source");
            var stagingRoot = Path.Combine(deviceRoot, "staging");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(stagingRoot);

            var context = new DeviceContext(
                "wb-1",
                "wt-1",
                "plc-1",
                root,
                worktreeRoot,
                deviceRoot,
                sourceRoot,
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
                Path.Combine(Context.SourceRoot, "metadata.json"),
                JsonSerializer.Serialize(new { schemaVersion = "1.0", components }));
        }

        public void WriteOverlay(string relativePath, string contents)
        {
            var path = WorkbenchPaths.ResolveRelative(Context.SourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void WriteSource(string relativePath, string contents) =>
            WriteOverlay(relativePath, contents);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
