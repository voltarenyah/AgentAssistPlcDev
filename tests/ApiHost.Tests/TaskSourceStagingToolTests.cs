using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Contracts.Engineering;
using Contracts.Sandbox;
using System.Text.Json;
using Xunit;

/// <summary>
/// Item 005: the agent's staging tool writes only through the shared <see cref="AgentSandbox"/>
/// approval card. Approving stages the requested objects for the active task through the coordinator's
/// committed-content baseline path; rejecting changes nothing; another device's object is refused; and
/// an object owned by another active task is reported instead of taken over silently.
/// </summary>
public sealed class TaskSourceStagingToolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"task-stage-tool-{Guid.NewGuid():N}");

    [Fact]
    public async Task ApprovingTheCallStagesTheRequestedObjectsForTheActiveTask()
    {
        var fixture = StageToolFixture.Create(root);
        var sandbox = new AgentSandbox(new SandboxPolicy(), 20, _ => Task.FromResult(ToolConfirmation.AllowOnce));
        var call = fixture.Call("""
            {"objects":[{"sourceObjectId":"block-main"},{"sourceObjectId":"device-1:block-unused"}]}
            """);

        var verdict = await sandbox.CheckAsync(call);

        Assert.Null(verdict); // approved: the call may proceed to the tool
        var result = await fixture.InvokeAsync(call.ArgumentsJson);

        var stages = fixture.ReadStages(StageToolFixture.TaskId);
        Assert.Equal(
            new[] { "device-1:block-main", "device-1:block-unused" },
            stages.Select(stage => stage.SourceObjectId).OrderBy(id => id, StringComparer.Ordinal));
        // The write went through StageTaskSourceObjectAsync, so each stage carries the committed
        // manifest's fingerprint evidence: the ADR-0003 baseline rule stays in one place.
        Assert.Contains("AAAA1111", stages.Single(stage => stage.SourceObjectId == "device-1:block-main").BaselineEvidenceJson);
        Assert.Contains("DDDD4444", stages.Single(stage => stage.SourceObjectId == "device-1:block-unused").BaselineEvidenceJson);
        Assert.Contains("device-1:block-main", result.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectingTheCallLeavesTheStageListAndTheTaskUnchanged()
    {
        var fixture = StageToolFixture.Create(root);
        var sandbox = new AgentSandbox(new SandboxPolicy(), 20, _ => Task.FromResult(ToolConfirmation.Deny));
        var call = fixture.Call("""
            {"objects":[{"sourceObjectId":"block-main"}]}
            """);

        var verdict = await sandbox.CheckAsync(call);

        Assert.NotNull(verdict);
        Assert.Contains("SANDBOX_USER_DENIED", verdict!.ErrorJson, StringComparison.Ordinal);
        // The loop never dispatches a refused call, so no stage exists — for this task or any other —
        // and the task row itself is untouched.
        Assert.Empty(fixture.ReadStages(StageToolFixture.TaskId));
        Assert.Empty(fixture.ReadWorktreeStages());
        var task = fixture.ReadTask(StageToolFixture.TaskId);
        Assert.Equal("Stage work", task.Title);
        Assert.Equal(GraphTaskStatus.Todo, task.Status);
    }

    [Fact]
    public async Task WithoutAnActiveTaskTheToolReportsTheMissingContext()
    {
        var fixture = StageToolFixture.Create(root, active: false);

        var error = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeAsync("""
            {"objects":[{"sourceObjectId":"block-main"}]}
            """));

        Assert.Equal("ACTIVE_TASK_REQUIRED", error.Code);
        Assert.Empty(fixture.ReadStages(StageToolFixture.TaskId));
        Assert.Empty(fixture.ReadStages(StageToolFixture.OtherTaskId));
    }

    [Fact]
    public async Task AForeignDeviceObjectIsRefusedAndTheCurrentOwnerIsReportedBeforeATakeOver()
    {
        var fixture = StageToolFixture.Create(root);

        // Another device's object is refused before anything is written.
        var foreign = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeAsync("""
            {"objects":[{"sourceObjectId":"device-2:block-main"}]}
            """));
        Assert.Equal("TASK_SOURCE_DEVICE_MISMATCH", foreign.Code);
        Assert.Empty(fixture.ReadStages(StageToolFixture.TaskId));

        // The other active task already owns the object.
        fixture.RegisterSourceObject("device-1:block-main");
        fixture.StageForOtherTask("device-1:block-main");

        var owned = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeAsync("""
            {"objects":[{"sourceObjectId":"block-main"}]}
            """));
        Assert.Equal("SOURCE_ALREADY_STAGED", owned.Code);
        Assert.Contains("Other work", owned.Message, StringComparison.Ordinal); // the owner's title
        Assert.Contains(StageToolFixture.OtherTaskId, owned.Message, StringComparison.Ordinal); // and its id

        // A claim naming the wrong owner is refused with the real owner, so a stale name can never
        // release a different task's stage.
        var wrongOwner = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeAsync("""
            {"objects":[{"sourceObjectId":"block-main","takeOverFromTaskId":"someone-else"}]}
            """));
        Assert.Equal("SOURCE_ALREADY_STAGED", wrongOwner.Code);
        Assert.Contains(StageToolFixture.OtherTaskId, wrongOwner.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.ReadStages(StageToolFixture.TaskId));
        Assert.Single(fixture.ReadStages(StageToolFixture.OtherTaskId)); // still owned

        // Naming the owner is the take-over rule: release the current owner first, then stage.
        var staged = await fixture.InvokeAsync($$"""
            {"objects":[{"sourceObjectId":"block-main","takeOverFromTaskId":"{{StageToolFixture.OtherTaskId}}"}]}
            """);

        Assert.Empty(fixture.ReadStages(StageToolFixture.OtherTaskId));
        var mine = Assert.Single(fixture.ReadStages(StageToolFixture.TaskId));
        Assert.Equal("device-1:block-main", mine.SourceObjectId);
        Assert.Contains(StageToolFixture.OtherTaskId, staged.GetRawText(), StringComparison.Ordinal); // reports what it took over
    }

    [Fact]
    public async Task TheApprovalCardCarriesTheFullArgumentsSoTheOwnerIsNamedBeforeTheWrite()
    {
        var fixture = StageToolFixture.Create(root);
        var arguments = """
            {"objects":[{"sourceObjectId":"device-1:block-main","takeOverFromTaskId":"task-other"}]}
            """;
        ToolConfirmationRequest? shown = null;
        var sandbox = new AgentSandbox(new SandboxPolicy(), 20, request =>
        {
            shown = request;
            return Task.FromResult(ToolConfirmation.Deny);
        });

        // The tool is classified destructive, which is what routes it into the confirmation path.
        Assert.Equal(SandboxTier.Destructive, new SandboxPolicy().Classify(TaskSourceStagingTool.ToolName));
        await sandbox.CheckAsync(new ChatToolCall("call-card", TaskSourceStagingTool.ToolName, arguments));

        Assert.NotNull(shown);
        // Full arguments, not the 160-character audit summary: the card must show every object and the
        // owner whose stage a take-over releases.
        Assert.Equal(arguments.Trim(), shown!.ArgumentsSummary);
    }

    /// <summary>
    /// Item 008. A live conversation named the block the way the knowledge base names it, and the colon
    /// in `block:Main` was read as a claiming device id. The knowledge vocabulary names the same PLC
    /// objects, so it resolves instead of being refused — with or without the device prefix the old
    /// remediation told the caller to add.
    /// </summary>
    [Theory]
    [InlineData("block:Main")]
    [InlineData("device-1:block:Main")]
    [InlineData("BLOCK:Main")]
    public async Task AKnowledgeNodeIdResolvesToTheSourceObjectItNames(string requested)
    {
        var fixture = StageToolFixture.Create(root);

        await fixture.InvokeAsync($$"""{"objects":[{"sourceObjectId":"{{requested}}"}]}""");

        var stage = Assert.Single(fixture.ReadStages(StageToolFixture.TaskId));
        Assert.Equal("device-1:block-main", stage.SourceObjectId);
    }

    /// <summary>A knowledge-graph element that is not a storable source object says so, and says where
    /// the real ids are, instead of blaming a device called `symbol`.</summary>
    [Theory]
    [InlineData("symbol:Motor", "symbol")]
    [InlineData("db-member:Motors:Speed", "db-member")]
    [InlineData("udt-member:Motor:Speed", "udt-member")]
    public async Task AKnowledgeElementIdIsReportedAsSuchInsteadOfAsAForeignDevice(string requested, string head)
    {
        var fixture = StageToolFixture.Create(root);

        var error = await Assert.ThrowsAsync<ToolCallException>(() =>
            fixture.InvokeAsync($$"""{"objects":[{"sourceObjectId":"{{requested}}"}]}"""));

        Assert.Equal("GRAPH_TARGET_NOT_FOUND", error.Code);
        Assert.Contains($"knowledge-graph element id ('{head}:')", error.Message, StringComparison.Ordinal);
        Assert.Contains(TaskSourceObjectListTool.ToolName, error.Remediation!, StringComparison.Ordinal);
        Assert.Empty(fixture.ReadStages(StageToolFixture.TaskId));
    }

    /// <summary>
    /// The regression the live conversation stalled on: a value carrying the correct device prefix was
    /// returned unvalidated, so the graph's own "Source object was not registered." surfaced through the
    /// agent loop as a code-less AGENT_TOOL_ERROR with no remediation. The tool now resolves — or
    /// refuses with its own code and the candidates.
    /// </summary>
    [Fact]
    public async Task AnUnresolvableIdKeepsItsCodeAndNamesTheCandidates()
    {
        var fixture = StageToolFixture.Create(root);

        var error = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeAsync("""
            {"objects":[{"sourceObjectId":"device-1:block-mian"}]}
            """));

        Assert.Equal("GRAPH_TARGET_NOT_FOUND", error.Code);
        Assert.Contains("block-mian", error.Message, StringComparison.Ordinal);
        // The bounded "did you mean": the id that nearly matched, and the tool that lists them all.
        Assert.Contains("block-main", error.Message, StringComparison.Ordinal);
        Assert.Contains(TaskSourceObjectListTool.ToolName, error.Remediation!, StringComparison.Ordinal);
        Assert.Empty(fixture.ReadStages(StageToolFixture.TaskId));
    }

    /// <summary>A shortened name is the likeliest wrong value a model produces; it comes back with the
    /// candidates rather than a bare failure.</summary>
    [Fact]
    public async Task AShortenedNameComesBackWithTheCandidates()
    {
        var fixture = StageToolFixture.Create(root);

        var error = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeAsync("""
            {"objects":[{"sourceObjectId":"Mai"}]}
            """));

        Assert.Equal("GRAPH_TARGET_NOT_FOUND", error.Code);
        Assert.Contains("block-main (Main)", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A rooted path is a caller mistake, not a device-qualified id: reading the drive letter
    /// as a device name would report "belongs to device 'C'".</summary>
    [Fact]
    public async Task AnAbsolutePathIsRefusedAsAPathNotAsAForeignDevice()
    {
        var fixture = StageToolFixture.Create(root);

        var error = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeAsync("""
            {"objects":[{"sourceObjectId":"C:\\exports\\Blocks\\Main.xml"}]}
            """));

        Assert.Equal("GRAPH_TARGET_NOT_FOUND", error.Code);
        Assert.Contains("absolute path", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The graph's constraint exception is not a ToolCallException, so an untranslated one reaches the
    /// agent loop as AGENT_TOOL_ERROR with no code and no remediation.
    /// </summary>
    [Fact]
    public async Task AGraphConstraintFailureKeepsItsCodeAndPointsAtTheListing()
    {
        var error = await Assert.ThrowsAsync<ToolCallException>(() =>
            TaskSourceStagingTool.TranslateGraphFailure(() =>
                throw new EngineeringGraphConstraintException("Source object was not registered.", "GRAPH_TARGET_NOT_FOUND")));

        Assert.Equal("GRAPH_TARGET_NOT_FOUND", error.Code);
        Assert.Equal("Source object was not registered.", error.Message);
        Assert.Contains(TaskSourceObjectListTool.ToolName, error.Remediation!, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>One device-bound worktree with two tasks and a committed-shape source manifest, wired
    /// to the real tool, graph factory, active-task service and coordinator.</summary>
    private sealed class StageToolFixture
    {
        public const string WorktreeId = "master-1";
        public const string DeviceId = "device-1";
        public const string TaskId = "task-stage";
        public const string OtherTaskId = "task-other";

        private const string ManifestJson = """
            {
              "schemaVersion": "1.0",
              "components": [
                {
                  "id": "block-main",
                  "name": "Main",
                  "category": "OB",
                  "exportedFile": "Blocks/Main.xml",
                  "fingerprints": { "Code": "AAAA1111", "Interface": "BBBB2222" }
                },
                {
                  "id": "block-unused",
                  "name": "Later",
                  "category": "FC",
                  "exportedFile": "Blocks/Later.xml",
                  "fingerprints": { "Code": "DDDD4444" }
                }
              ]
            }
            """;

        private readonly string workbenchRoot;
        private readonly string workbenchId;

        private StageToolFixture(string workbenchRoot, string workbenchId, DeviceContext device, AgentToolSpec spec)
        {
            this.workbenchRoot = workbenchRoot;
            this.workbenchId = workbenchId;
            Device = device;
            Spec = spec;
        }

        public DeviceContext Device { get; }
        public AgentToolSpec Spec { get; }

        public static StageToolFixture Create(string parent, bool active = true)
        {
            var fixtureRoot = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            var store = new AtomicJsonStore();
            var catalog = new WorkbenchCatalog(store, fixtureRoot);
            var workbench = catalog.RegisterWorktree(
                catalog.Create("Line", null),
                new WorkbenchWorktreeRegistration(WorktreeId, "master", "master", "master"));
            var device = WorkbenchPaths.ResolveDevice(
                workbench.WorkbenchId, workbench.RootPath, WorktreeId, "master", DeviceId, "PLC_1");
            Directory.CreateDirectory(device.SourceRoot);
            store.Write(Path.Combine(device.WorktreeRoot, "worktree.json"), new WorktreeMetadata(
                "1.2", WorktreeId, workbench.WorkbenchId, "master", "master",
                DateTimeOffset.UtcNow.ToString("O"), "head-1", "project-1", null, new[] { DeviceId }, null));
            store.Write(Path.Combine(device.DeviceRoot, "device.json"), new DeviceMetadata(
                "1.2", DeviceId, WorktreeId, "PLC_1", "project-1", null, null, null,
                new KnowledgeState(false, new Dictionary<string, string>(), null), Array.Empty<DeviceImportRecord>()));
            File.WriteAllText(Path.Combine(device.SourceRoot, "metadata.json"), ManifestJson);

            using (var graphStore = new EngineeringGraphStore(workbench.RootPath))
            {
                var graph = new EngineeringGraphService(graphStore, workbench.WorkbenchId, id => id == WorktreeId);
                graph.CreateTask(TaskId, GraphTaskScopeKind.Worktree, WorktreeId, "Stage work",
                    GraphTaskType.Feature, intent: "intent", expectedResult: "result", deviceId: DeviceId);
                graph.CreateTask(OtherTaskId, GraphTaskScopeKind.Worktree, WorktreeId, "Other work",
                    GraphTaskType.Issue, intent: "intent", expectedResult: "result", deviceId: DeviceId);
            }

            var state = new WorkbenchApiState(catalog, store);
            state.Add(workbench);
            var activeTasks = new ActiveTaskContextService();
            if (active)
            {
                using var graphStore = new EngineeringGraphStore(workbench.RootPath);
                activeTasks.Select(
                    new EngineeringGraphService(graphStore, workbench.WorkbenchId, id => id == WorktreeId),
                    WorktreeId, TaskId);
            }

            var versionControl = new ScriptedCaller()
                .Respond("vc_show_file", new ShowFileResult { Content = ManifestJson });
            var coordinator = new WorkbenchCoordinator(
                new ScriptedCaller(), new ScriptedCaller(), versionControl, catalog, store,
                new DeviceReconciler(), new DeviceSourceResolver(_ => { }));
            var tool = new TaskSourceStagingTool(state, new EngineeringGraphApiFactory(), activeTasks, coordinator);
            return new StageToolFixture(
                workbench.RootPath, workbench.WorkbenchId, device,
                TaskSourceStagingTool.CreateSpec(tool, () => device));
        }

        public ChatToolCall Call(string argumentsJson) =>
            new("call-stage", TaskSourceStagingTool.ToolName, argumentsJson.Trim());

        public async Task<JsonElement> InvokeAsync(string argumentsJson)
        {
            using var arguments = JsonDocument.Parse(argumentsJson);
            return await Spec.Caller.CallAsync<JsonElement>(
                TaskSourceStagingTool.ToolName, arguments.RootElement, CancellationToken.None);
        }

        public void RegisterSourceObject(string sourceObjectId)
        {
            using var graphStore = new EngineeringGraphStore(workbenchRoot);
            new EngineeringGraphService(graphStore, workbenchId, id => id == WorktreeId)
                .RegisterEntity(new GraphEntity(GraphEntityKind.SourceObject, sourceObjectId,
                    workbenchId, WorktreeId, DeviceId, "Blocks/Main.xml"));
        }

        public void StageForOtherTask(string sourceObjectId)
        {
            using var graphStore = new EngineeringGraphStore(workbenchRoot);
            new EngineeringGraphService(graphStore, workbenchId, id => id == WorktreeId)
                .StageSourceObject(OtherTaskId, sourceObjectId, "baseline-other");
        }

        public IReadOnlyList<TaskSourceStage> ReadStages(string taskId)
        {
            using var graphStore = new EngineeringGraphStore(workbenchRoot);
            return new EngineeringGraphService(graphStore, workbenchId, id => id == WorktreeId)
                .ListActiveStages(taskId);
        }

        public IReadOnlyList<WorktreeSourceStage> ReadWorktreeStages()
        {
            using var graphStore = new EngineeringGraphStore(workbenchRoot);
            return new EngineeringGraphService(graphStore, workbenchId, id => id == WorktreeId)
                .ListWorktreeActiveStages(WorktreeId);
        }

        public GraphTask ReadTask(string taskId)
        {
            using var graphStore = new EngineeringGraphStore(workbenchRoot);
            return new EngineeringGraphService(graphStore, workbenchId, id => id == WorktreeId)
                .FindTask(taskId)!;
        }
    }

    /// <summary>Scripted in-process caller: one response per tool, every call recorded.</summary>
    private sealed class ScriptedCaller : IMcpToolCaller
    {
        private readonly Dictionary<string, object> responses = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = new();

        public ScriptedCaller Respond(string tool, object response)
        {
            responses[tool] = response;
            return this;
        }

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (!responses.TryGetValue(tool, out var response))
            {
                throw new InvalidOperationException($"ScriptedCaller: no scripted response for '{tool}'.");
            }
            return Task.FromResult((T)response);
        }
    }
}
