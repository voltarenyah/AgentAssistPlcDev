using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Contracts.Engineering;
using System.Text.Json;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// ADR-0003: a task stage baseline is bound to the object's committed Git content, so a stage
/// created by hand acquires its fingerprint evidence from the per-device export manifest at Git
/// HEAD — never from a live TIA capture, and never from the working-tree manifest.
/// </summary>
public sealed class TaskStageBaselineTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"task-stage-baseline-{Guid.NewGuid():N}");

    [Fact]
    public async Task StageBindsTheBaselineToTheManifestAtGitHeadNotTheWorkingTree()
    {
        var fixture = TaskStageFixture.Create(root);
        // The working tree carries an export that has not been committed yet; it must not become
        // the baseline. The version-control read is the committed blob, which the fake returns.
        fixture.WriteWorkingTreeManifest(TaskStageFixture.ManifestJson.Replace("AAAA1111", "WORKTREE"));
        var versionControl = new FakeToolCaller()
            .Respond("vc_show_file", new ShowFileResult { Content = TaskStageFixture.ManifestJson });
        var coordinator = fixture.CreateCoordinator(versionControl);

        var stage = await coordinator.StageTaskSourceObjectAsync(
            TaskStageFixture.WorkbenchId, TaskStageFixture.WorktreeId, fixture.TaskId, "device-1:block-main");

        // The manifest is read at HEAD through the blob, not from the worktree file on disk.
        var read = Assert.Single(versionControl.CallArgs["vc_show_file"]);
        Assert.Equal("devices/PLC_1/source/metadata.json", Property<string>(read, "filePath"));
        Assert.Null(Property<string?>(read, "commitSha"));

        // The stored row equals the manifest's fingerprint evidence at Git HEAD, field for field.
        var persisted = Assert.Single(fixture.ReadStages(fixture.TaskId));
        Assert.Equal(stage.BaselineEvidenceJson, persisted.BaselineEvidenceJson);
        var evidence = JsonSerializer.Deserialize<ManagedSourceEvidenceObject>(persisted.BaselineEvidenceJson!)!;
        Assert.Equal("block-main", evidence.Id);
        Assert.Equal("Main", evidence.Name);
        Assert.Equal("Program blocks/Main", evidence.SourcePath);
        Assert.Equal("OB", evidence.Category);
        Assert.Equal(ManagedSourceEvidenceKind.StandardBlock, evidence.Kind);
        Assert.Equal(ManagedSourceEvidenceReadState.Readable, evidence.ReadState);
        Assert.Equal("AAAA1111", evidence.Fingerprints!["Code"]);
        Assert.Equal("BBBB2222", evidence.Fingerprints["Interface"]);
        Assert.Equal("CCCC3333", evidence.Fingerprints["Comments"]);
        Assert.DoesNotContain("WORKTREE", persisted.BaselineEvidenceJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StageKeepsANullBaselineWhenTheObjectHasNoCommittedManifestContent()
    {
        var fixture = TaskStageFixture.Create(root);
        // {content: null} is vc_show_file's documented answer when the commit or path does not
        // exist — a worktree whose manifest has never been committed.
        var versionControl = new FakeToolCaller()
            .Respond("vc_show_file", new ShowFileResult { Content = null });
        var coordinator = fixture.CreateCoordinator(versionControl);

        var stage = await coordinator.StageTaskSourceObjectAsync(
            TaskStageFixture.WorkbenchId, TaskStageFixture.WorktreeId, fixture.TaskId, "device-1:block-main");

        Assert.Null(stage.BaselineEvidenceJson);
        var persisted = Assert.Single(fixture.ReadStages(fixture.TaskId));
        Assert.Null(persisted.BaselineEvidenceJson);
    }

    [Fact]
    public async Task StageKeepsANullBaselineForAnObjectTheCommittedManifestDoesNotList()
    {
        var fixture = TaskStageFixture.Create(root);
        var versionControl = new FakeToolCaller()
            .Respond("vc_show_file", new ShowFileResult
            {
                // A committed manifest that predates this object: it exists only in TIA.
                Content = """{ "components": [ { "id": "other", "name": "Other", "category": "FC", "exportedFile": "Blocks/Other.xml" } ] }""",
            });
        var coordinator = fixture.CreateCoordinator(versionControl);

        var stage = await coordinator.StageTaskSourceObjectAsync(
            TaskStageFixture.WorkbenchId, TaskStageFixture.WorktreeId, fixture.TaskId, "device-1:block-main");

        Assert.Null(stage.BaselineEvidenceJson);
    }

    [Fact]
    public async Task CompareReportsTheStagedObjectsDifferenceAsACommitReadyRow()
    {
        var fixture = TaskStageFixture.Create(root);
        var versionControl = new FakeToolCaller()
            .Respond("vc_show_file", new ShowFileResult { Content = TaskStageFixture.ManifestJson })
            .Respond("vc_log", new ConsistencyLogResult { Commits = [new ConsistencyCommit { Sha = "head-1" }] });
        var engineering = fixture.ScriptCompareEvidence();
        var coordinator = fixture.CreateCoordinator(versionControl, engineering);
        await coordinator.StageTaskSourceObjectAsync(
            TaskStageFixture.WorkbenchId, TaskStageFixture.WorktreeId, fixture.TaskId, "device-1:block-main");
        // A second registered object of the same device is deliberately not staged (AC-001).
        fixture.RegisterSourceObject("device-1:block-unstaged");

        var comparison = await coordinator.CompareTaskWithTiaAsync(
            TaskStageFixture.WorkbenchId, TaskStageFixture.WorktreeId, fixture.TaskId);

        // A scoped result is a normal selectable difference row — the shape a project-wide scan
        // produces — so one difference list, one selection, and one accept path serve both.
        var difference = Assert.Single(comparison.Differences);
        Assert.Equal("devices/PLC_1/source/Blocks/Main.xml", difference.RelativePath);
        Assert.Equal(SourceDifferenceKind.Changed, difference.Kind);
        Assert.True(difference.Supported);
        // "Which part of the block changed" is the per-component evidence, not a summary count.
        Assert.False(difference.FingerprintComponents!["Code"].Matches);
        Assert.True(difference.FingerprintComponents["Interface"].Matches);
        // The row carries the compared identity the accept path verifies before it copies (#111).
        Assert.False(string.IsNullOrWhiteSpace(difference.TiaFingerprint));
        Assert.Empty(comparison.StageProblems!);
        Assert.Equal(fixture.TaskId, comparison.ComparedTaskId);
        Assert.Equal("head-1", comparison.MasterSha);
        // A task-scoped comparison is not a project-wide verdict: it checks no hardware.
        Assert.False(comparison.HardwareChecked);
        Assert.Null(comparison.Hardware);
        var request = Assert.Single(engineering.CallArgs["compare_source_evidence"]);
        Assert.Equal(new[] { "block-main" }, Property<string[]>(request, "sourceObjectIds"));
        // The comparison baseline handed to TIA is the committed manifest evidence.
        var baseline = Property<SourceEvidenceSnapshot>(request, "baseline");
        var baselineObject = Assert.Single(baseline.Objects);
        Assert.Equal("block-main", baselineObject.Id);
        Assert.Equal("AAAA1111", baselineObject.Fingerprints!["Code"]);
    }

    [Fact]
    public async Task CompareStillReportsBaselineMissingForAnObjectWithNoCommittedContent()
    {
        var fixture = TaskStageFixture.Create(root);
        var versionControl = new FakeToolCaller()
            .Respond("vc_show_file", new ShowFileResult { Content = null })
            .Respond("vc_log", new ConsistencyLogResult { Commits = [new ConsistencyCommit { Sha = "head-1" }] });
        var engineering = new FakeToolCaller();
        var coordinator = fixture.CreateCoordinator(versionControl, engineering);
        await coordinator.StageTaskSourceObjectAsync(
            TaskStageFixture.WorkbenchId, TaskStageFixture.WorktreeId, fixture.TaskId, "device-1:block-main");

        var comparison = await coordinator.CompareTaskWithTiaAsync(
            TaskStageFixture.WorkbenchId, TaskStageFixture.WorktreeId, fixture.TaskId);

        var problem = Assert.Single(comparison.StageProblems!);
        Assert.Equal("TASK_STAGE_BASELINE_MISSING", problem.Code);
        Assert.Equal("device-1:block-main", problem.SourceObjectId);
        // The problem is readable and nothing is selectable or certified in its place.
        Assert.Empty(comparison.Differences);
        Assert.NotEqual(ConsistencyState.Consistent, comparison.State);
        // Nothing is read from TIA for a stage that has no baseline to compare against.
        Assert.DoesNotContain("compare_source_evidence", engineering.Calls);
    }

    [Fact]
    public async Task ProjectScopedTaskCannotStageSourceObjects()
    {
        var fixture = TaskStageFixture.Create(root);
        var coordinator = fixture.CreateCoordinator(new FakeToolCaller());

        var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
            coordinator.StageTaskSourceObjectAsync(
                TaskStageFixture.WorkbenchId, TaskStageFixture.WorktreeId, fixture.ProjectTaskId, "device-1:block-main"));

        Assert.Equal("TASK_NOT_FOUND", error.Code);
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

    private sealed class TaskStageFixture
    {
        public const string WorkbenchId = "wb-task-stage";
        public const string WorktreeId = "master-1";

        /// <summary>The committed manifest blob, exactly as the fake version-control layer returns
        /// it for devices/PLC_1/source/metadata.json at Git HEAD.</summary>
        public const string ManifestJson = """
            {
              "schemaVersion": "1.0",
              "components": [
                {
                  "id": "block-main",
                  "name": "Main",
                  "category": "OB",
                  "sourcePath": "Program blocks/Main",
                  "exportedFile": "Blocks/Main.xml",
                  "fingerprints": { "Code": "AAAA1111", "Interface": "BBBB2222", "Comments": "CCCC3333" }
                },
                {
                  "id": "block-unstaged",
                  "name": "Later",
                  "category": "FC",
                  "exportedFile": "Blocks/Later.xml",
                  "fingerprints": { "Code": "DDDD4444" }
                }
              ]
            }
            """;

        private readonly AtomicJsonStore store = new();

        private TaskStageFixture(string root, string managedProjectPath)
        {
            Root = root;
            ManagedProjectPath = managedProjectPath;
        }

        public string Root { get; }
        public string ManagedProjectPath { get; }
        public string MasterRoot => Path.Combine(Root, "worktrees", "master");
        public string SourceRoot => Path.Combine(MasterRoot, "devices", "PLC_1", "source");
        public string TaskId { get; private set; } = string.Empty;
        public string ProjectTaskId { get; private set; } = string.Empty;

        public static TaskStageFixture Create(string parent)
        {
            var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            var store = new AtomicJsonStore();
            var masterRoot = Path.Combine(root, "worktrees", "master");
            var tiaStore = WorkbenchPaths.ResolveTiaStore(masterRoot);
            Directory.CreateDirectory(tiaStore);
            var managedPath = Path.Combine(tiaStore, "Line.ap17");
            File.WriteAllText(managedPath, "managed project");

            store.Write(Path.Combine(root, "workbench.json"), new WorkbenchMetadata(
                "1.2", WorkbenchId, "wb", "now", root, Path.Combine(root, "repository.git"),
                "project-1", managedPath,
                new[] { new WorkbenchWorktreeRegistration(WorktreeId, "master", "master", "master") },
                ManagedTiaProjectPath: managedPath));
            store.Write(Path.Combine(masterRoot, "worktree.json"), new WorktreeMetadata(
                "1.2", WorktreeId, WorkbenchId, "master", "master", "now", "head-1",
                "project-1", managedPath, new[] { "device-1" }, null,
                ManagedTiaProjectPath: managedPath));

            var context = WorkbenchPaths.ResolveDevice(WorkbenchId, root, WorktreeId, "master", "device-1", "PLC_1");
            Directory.CreateDirectory(Path.Combine(context.SourceRoot, "Blocks"));
            File.WriteAllText(Path.Combine(context.SourceRoot, "Blocks", "Main.xml"),
                "<Document><SW.Blocks.OB ID=\"1\" /></Document>");
            store.Write(Path.Combine(context.DeviceRoot, "device.json"), new DeviceMetadata(
                "1.2", "device-1", WorktreeId, "PLC_1", "project-1", null, null, null,
                new KnowledgeState(false, new Dictionary<string, string>(), null),
                Array.Empty<DeviceImportRecord>()));

            var fixture = new TaskStageFixture(root, managedPath);
            fixture.CreateTasks();
            return fixture;
        }

        /// <summary>Creates the device-bound worktree task and a project-scope task through the same
        /// graph service the coordinator opens, then registers the object the stage route would.</summary>
        private void CreateTasks()
        {
            using var graphStore = new EngineeringGraphStore(Root);
            var graph = new EngineeringGraphService(graphStore, WorkbenchId, id => id == WorktreeId);
            TaskId = graph.CreateTask("task-stage", GraphTaskScopeKind.Worktree, WorktreeId, "Stage work",
                GraphTaskType.Feature, intent: "intent", expectedResult: "result", deviceId: "device-1").TaskId;
            ProjectTaskId = graph.CreateTask("task-project", GraphTaskScopeKind.Project, null, "Project work",
                GraphTaskType.Issue, intent: "intent", expectedResult: "result").TaskId;
            RegisterSourceObject("device-1:block-main");
        }

        public void RegisterSourceObject(string sourceObjectId)
        {
            using var graphStore = new EngineeringGraphStore(Root);
            new EngineeringGraphService(graphStore, WorkbenchId, id => id == WorktreeId)
                .RegisterEntity(new GraphEntity(GraphEntityKind.SourceObject, sourceObjectId, WorkbenchId, WorktreeId, "device-1", "Blocks/Main.xml"));
        }

        public IReadOnlyList<TaskSourceStage> ReadStages(string taskId)
        {
            using var graphStore = new EngineeringGraphStore(Root);
            return new EngineeringGraphService(graphStore, WorkbenchId, id => id == WorktreeId).ListActiveStages(taskId);
        }

        public void WriteWorkingTreeManifest(string json) =>
            File.WriteAllText(Path.Combine(SourceRoot, "metadata.json"), json);

        public WorkbenchCoordinator CreateCoordinator(FakeToolCaller versionControl, FakeToolCaller? engineering = null)
        {
            var catalog = new WorkbenchCatalog(store, Path.Combine(Root, "catalog"));
            var coordinator = new WorkbenchCoordinator(
                engineering ?? new FakeToolCaller(),
                new FakeToolCaller(),
                versionControl,
                catalog,
                store,
                new DeviceReconciler(),
                new DeviceSourceResolver(_ => { }));
            coordinator.RegisterWorkbench(catalog.Load(Root));
            return coordinator;
        }

        /// <summary>Scripts the engineering side of a task comparison: the registered project is
        /// already active, and the scoped capture echoes the baseline back with one changed
        /// candidate, exporting the candidate XML the surface compares and later commits — the same
        /// temporary candidate export a project-wide scan produces.</summary>
        public FakeToolCaller ScriptCompareEvidence()
        {
            var engineering = new FakeToolCaller();
            engineering
                .Respond("get_project_info", new ProjectInfo
                {
                    Name = "Line",
                    Path = ManagedProjectPath,
                    PlcDevices = ["PLC_1"],
                })
                .Respond("compare_source_evidence", args =>
                {
                    var baseline = (SourceEvidenceSnapshot)args.GetType().GetProperty("baseline")!.GetValue(args)!;
                    var outputDir = (string)args.GetType().GetProperty("outputDir")!.GetValue(args)!;
                    var changed = baseline.Objects.Select(item => new ManagedSourceEvidenceObject
                    {
                        Id = item.Id,
                        Name = item.Name,
                        SourcePath = item.SourcePath,
                        Category = item.Category,
                        Kind = item.Kind,
                        ReadState = item.ReadState,
                        Fingerprints = new FingerprintSet { ["Code"] = "changed", ["Interface"] = "BBBB2222", ["Comments"] = "CCCC3333" },
                    }).ToArray();
                    // The candidate export mirrors the source tree layout, as the TIA adapter's does.
                    var exportPath = Path.Combine(outputDir, "Blocks", "Main.xml");
                    Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
                    File.WriteAllText(exportPath, "<Document><SW.Blocks.OB ID=\"1\"><Edited>changed</Edited></SW.Blocks.OB></Document>");
                    return new SourceEvidenceCaptureResult
                    {
                        Snapshot = new SourceEvidenceSnapshot
                        {
                            PlcName = "PLC_1",
                            Checksum = new PlcChecksumInfo { PlcName = "PLC_1", SoftwareChecksum = "PLC_1:checksum-1" },
                            Objects = changed,
                        },
                        Candidates = changed.Select(item => new SourceEvidenceCandidate
                        {
                            Id = item.Id,
                            Reason = SourceEvidenceCandidateReason.FingerprintChanged,
                            RequiresXmlExport = true,
                            Baseline = baseline.Objects.Single(baselineObject => baselineObject.Id == item.Id),
                            Live = item,
                        }).ToArray(),
                        CandidateExports = changed.Select(item => new SourceEvidenceCandidateExport
                        {
                            Id = item.Id,
                            SourcePath = item.SourcePath,
                            Export = new ExportResult { Success = true, Path = exportPath, BlockName = item.Name },
                        }).ToArray(),
                    };
                });
            return engineering;
        }
    }
}
