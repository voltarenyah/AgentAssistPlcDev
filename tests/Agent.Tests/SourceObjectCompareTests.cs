using Agent.Mcp;
using Agent.Workbench;
using Contracts.Engineering;
using System.Text.Json;
using Xunit;

namespace Agent.Tests;

public sealed class SourceObjectCompareTests
{
    private const string LocalXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <Document>
          <SW.Blocks.OB ID="0">
            <AttributeList><Name>Main</Name></AttributeList>
          </SW.Blocks.OB>
        </Document>
        """;

    [Fact]
    public async Task CompareReportsSameWhenOnlyTheCreatedTimestampDiffers()
    {
        using var fixture = CompareFixture.Create(LocalXml);
        var tiaXml = LocalXml.Replace(
            "  </SW.Blocks.OB>",
            "    <Created>2026-08-01T00:00:00</Created>\n  </SW.Blocks.OB>");
        var engineering = fixture.EngineeringWithActiveProject()
            .Respond("export_source_object", args =>
            {
                fixture.WriteStaged("Blocks/Main [OB1].xml", tiaXml);
                return new ExportResult { BlockName = "Main", Success = true };
            });
        var coordinator = fixture.CreateCoordinator(engineering);

        var comparison = await coordinator.CompareSourceObjectWithTiaAsync(
            fixture.Context, "Blocks/Main [OB1].xml");

        Assert.True(comparison.Same);
        Assert.All(comparison.DiffLines, line => Assert.Equal(DiffLine.Same, line.Kind));
        Assert.Equal("Main", comparison.Name);
        Assert.Equal("OB", comparison.Category);
        Assert.Equal(comparison.LocalHash, comparison.TiaHash);
        Assert.DoesNotContain("connect", engineering.Calls);
        var exportArgs = Assert.Single(engineering.CallArgs["export_source_object"]);
        Assert.Equal("Main", Property(exportArgs, "name"));
        Assert.Equal("OB", Property(exportArgs, "category"));
        Assert.Equal("PLC_1", Property(exportArgs, "plcName"));
        Assert.Equal(fixture.Context.StagingRoot, Property(exportArgs, "outputDir"));
    }

    [Fact]
    public async Task CompareProducesALineDiffWhenContentDiffers()
    {
        using var fixture = CompareFixture.Create(LocalXml);
        var tiaXml = LocalXml.Replace("</Document>", "  <Network />\n</Document>");
        var engineering = fixture.EngineeringWithActiveProject()
            .Respond("export_source_object", args =>
            {
                fixture.WriteStaged("Blocks/Main [OB1].xml", tiaXml);
                return new ExportResult { BlockName = "Main", Success = true };
            });
        var coordinator = fixture.CreateCoordinator(engineering);

        var comparison = await coordinator.CompareSourceObjectWithTiaAsync(
            fixture.Context, "Blocks/Main [OB1].xml");

        Assert.False(comparison.Same);
        Assert.Contains(comparison.DiffLines, line =>
            line.Kind == DiffLine.Added && line.Text.Contains("<Network />"));
        Assert.NotEqual(comparison.LocalHash, comparison.TiaHash);
    }

    [Fact]
    public async Task AcceptCopiesTheStagedTiaVersionOverLocalAndMarksKnowledgeStale()
    {
        using var fixture = CompareFixture.Create(LocalXml);
        var tiaXml = LocalXml.Replace("</Document>", "  <Network />\n</Document>");
        var engineering = fixture.EngineeringWithActiveProject()
            .Respond("export_source_object", args =>
            {
                fixture.WriteStaged("Blocks/Main [OB1].xml", tiaXml);
                return new ExportResult { BlockName = "Main", Success = true };
            });
        var coordinator = fixture.CreateCoordinator(engineering);
        var comparison = await coordinator.CompareSourceObjectWithTiaAsync(
            fixture.Context, "Blocks/Main [OB1].xml");

        var result = await coordinator.AcceptTiaSourceObjectAsync(fixture.Context, comparison.ComparisonId);

        Assert.True(result.Success);
        Assert.Equal("Blocks/Main [OB1].xml", result.RelativePath);
        Assert.Equal(tiaXml, fixture.ReadLocal("Blocks/Main [OB1].xml"));
        var metadata = fixture.ReadDeviceMetadata();
        Assert.True(metadata.Knowledge.Stale);
        Assert.True(metadata.Knowledge.BaselineStale);
    }

    [Fact]
    public async Task PushImportsTheLocalFileIntoTia()
    {
        using var fixture = CompareFixture.Create(LocalXml);
        var engineering = fixture.EngineeringWithActiveProject(getProjectInfoResponses: 2)
            .Respond("export_source_object", args =>
            {
                fixture.WriteStaged("Blocks/Main [OB1].xml", LocalXml);
                return new ExportResult { BlockName = "Main", Success = true };
            })
            .Respond("import_source_object", _ => new { });
        var coordinator = fixture.CreateCoordinator(engineering);
        var comparison = await coordinator.CompareSourceObjectWithTiaAsync(
            fixture.Context, "Blocks/Main [OB1].xml");

        var result = await coordinator.PushSourceObjectToTiaAsync(fixture.Context, comparison.ComparisonId);

        Assert.True(result.Success);
        var importArgs = Assert.Single(engineering.CallArgs["import_source_object"]);
        Assert.Equal("Blocks/Main [OB1].xml", Property(importArgs, "relativePath"));
        Assert.Equal("PLC_1", Property(importArgs, "plcName"));
        Assert.Equal(
            fixture.LocalFile("Blocks/Main [OB1].xml"),
            Property(importArgs, "xmlFilePath"));
    }

    [Fact]
    public async Task PushReportsAnOpenEditorConflictAsAnUnsuccessfulResult()
    {
        using var fixture = CompareFixture.Create(LocalXml);
        var engineering = fixture.EngineeringWithActiveProject(getProjectInfoResponses: 2)
            .Respond("export_source_object", args =>
            {
                fixture.WriteStaged("Blocks/Main [OB1].xml", LocalXml);
                return new ExportResult { BlockName = "Main", Success = true };
            })
            .Fail("import_source_object", "BLOCK_OPEN_IN_EDITOR", "Block 'Main' is open in an editor.");
        var coordinator = fixture.CreateCoordinator(engineering);
        var comparison = await coordinator.CompareSourceObjectWithTiaAsync(
            fixture.Context, "Blocks/Main [OB1].xml");

        var result = await coordinator.PushSourceObjectToTiaAsync(fixture.Context, comparison.ComparisonId);

        Assert.False(result.Success);
        Assert.Contains("open in an editor", result.Message);
    }

    [Fact]
    public async Task AcceptWithoutAKnownComparisonIsRejected()
    {
        using var fixture = CompareFixture.Create(LocalXml);
        var coordinator = fixture.CreateCoordinator(new FakeToolCaller());

        var exception = await Assert.ThrowsAsync<WorkbenchLifecycleException>(
            () => coordinator.AcceptTiaSourceObjectAsync(fixture.Context, "missing"));

        Assert.Equal("COMPARISON_NOT_FOUND", exception.Code);
    }

    [Fact]
    public async Task CompareFailsWhenTheLocalSourceIsMissing()
    {
        using var fixture = CompareFixture.Create(LocalXml);
        var coordinator = fixture.CreateCoordinator(new FakeToolCaller());

        var exception = await Assert.ThrowsAsync<WorkbenchLifecycleException>(
            () => coordinator.CompareSourceObjectWithTiaAsync(fixture.Context, "Blocks/Gone [OB9].xml"));

        Assert.Equal("LOCAL_SOURCE_MISSING", exception.Code);
    }

    [Fact]
    public async Task CompareDerivesIdentityFromThePathWhenNoManifestExists()
    {
        using var fixture = CompareFixture.Create(LocalXml);
        var engineering = fixture.EngineeringWithActiveProject()
            .Respond("export_source_object", args =>
            {
                fixture.WriteStaged("Blocks/Main [OB1].xml", LocalXml);
                return new ExportResult { BlockName = "Main", Success = true };
            });
        var coordinator = fixture.CreateCoordinator(engineering);

        // No metadata.json in the source root — identity comes from the "Name [OB1]" suffix.
        var comparison = await coordinator.CompareSourceObjectWithTiaAsync(
            fixture.Context, "Blocks/Main [OB1].xml");

        Assert.True(comparison.Same);
        var exportArgs = Assert.Single(engineering.CallArgs["export_source_object"]);
        Assert.Equal("Main", Property(exportArgs, "name"));
        Assert.Equal("OB", Property(exportArgs, "category"));
    }

    [Fact]
    public async Task OpenInTiaEnsuresTheProjectAndShowsTheEditor()
    {
        using var fixture = CompareFixture.Create(LocalXml);
        var engineering = fixture.EngineeringWithActiveProject()
            .Respond("open_source_object_in_editor", _ => new OpenInEditorResult
            {
                Name = "Main",
                Category = "OB",
                PlcName = "PLC_1",
                Opened = true,
            });
        var coordinator = fixture.CreateCoordinator(engineering);

        var result = await coordinator.OpenSourceObjectInTiaAsync(
            fixture.Context, "Blocks/Main [OB1].xml");

        Assert.True(result.Opened);
        Assert.Equal("Main", result.Name);
        var openArgs = Assert.Single(engineering.CallArgs["open_source_object_in_editor"]);
        Assert.Equal("Main", Property(openArgs, "name"));
        Assert.Equal("OB", Property(openArgs, "category"));
        Assert.Equal("PLC_1", Property(openArgs, "plcName"));
    }

    private static object? Property(object args, string name) =>
        args.GetType().GetProperty(name)?.GetValue(args);

    private sealed class CompareFixture : IDisposable
    {
        private CompareFixture(string root, DeviceContext context, string projectPath, AtomicJsonStore store)
        {
            Root = root;
            Context = context;
            ProjectPath = projectPath;
            Store = store;
        }

        public string Root { get; }
        public DeviceContext Context { get; }
        public string ProjectPath { get; }
        public AtomicJsonStore Store { get; }

        public static CompareFixture Create(string localXml)
        {
            var root = Path.Combine(Path.GetTempPath(), $"source-compare-tests-{Guid.NewGuid():N}");
            var worktreeRoot = Path.Combine(root, "worktrees", "main");
            var deviceRoot = Path.Combine(worktreeRoot, "devices", "plc-1");
            var sourceRoot = Path.Combine(deviceRoot, "source");
            var stagingRoot = Path.Combine(deviceRoot, "staging");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(stagingRoot);
            var projectPath = Path.Combine(root, "tia", "Demo.ap17");

            var store = new AtomicJsonStore();
            store.Write(
                Path.Combine(worktreeRoot, "worktree.json"),
                new WorktreeMetadata(
                    WorkbenchSchema.CurrentVersion,
                    "wt-1",
                    "wb-1",
                    "main",
                    "master",
                    "2026-01-01T00:00:00Z",
                    null,
                    null,
                    projectPath,
                    new[] { "plc-1" },
                    null));
            store.Write(
                Path.Combine(deviceRoot, "device.json"),
                new DeviceMetadata(
                    WorkbenchSchema.CurrentVersion,
                    "plc-1",
                    "wt-1",
                    "PLC_1",
                    "engineering-plc-1",
                    null,
                    null,
                    null,
                    new KnowledgeState(false, new Dictionary<string, string>(), null),
                    Array.Empty<DeviceImportRecord>()));

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
            var blocksDir = Directory.CreateDirectory(Path.Combine(sourceRoot, "Blocks"));
            File.WriteAllText(Path.Combine(blocksDir.FullName, "Main [OB1].xml"), localXml);

            return new CompareFixture(root, context, projectPath, store);
        }

        /// <summary>Engineering caller with the worktree project already active in TIA.</summary>
        public FakeToolCaller EngineeringWithActiveProject(int getProjectInfoResponses = 1)
        {
            var caller = new FakeToolCaller();
            for (var i = 0; i < getProjectInfoResponses; i++)
            {
                caller.Respond("get_project_info", new ProjectInfo { Path = ProjectPath });
            }

            return caller;
        }

        public WorkbenchCoordinator CreateCoordinator(IMcpToolCaller engineering)
        {
            var catalog = new WorkbenchCatalog(Store, Path.Combine(Root, "catalog"));
            return new WorkbenchCoordinator(
                engineering,
                new FakeToolCaller(),
                new FakeToolCaller(),
                catalog,
                Store,
                new DeviceReconciler(),
                new DeviceSourceResolver(_ => { }));
        }

        public string LocalFile(string relativePath) =>
            Path.Combine(Context.SourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public string ReadLocal(string relativePath) => File.ReadAllText(LocalFile(relativePath));

        public void WriteStaged(string relativePath, string contents)
        {
            var path = Path.Combine(Context.StagingRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public DeviceMetadata ReadDeviceMetadata() =>
            Store.Read<DeviceMetadata>(Path.Combine(Context.DeviceRoot, "device.json"));

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
