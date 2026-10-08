using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Contracts.Sandbox;
using System.Text.Json;
using Xunit;

/// <summary>
/// The device chat's task-creation tool: the PLC/knowledge agent records a finding it established in
/// the conversation as a device-bound worktree task. The task always targets the conversation's own
/// worktree and device (never arguments the model supplies), the brief sections the user asked for
/// (background, evidence, proposed solutions) land in the task's own description, and the call is
/// refused until the user approves it on the shared <see cref="AgentSandbox"/> card.
/// </summary>
public sealed class TaskCreationToolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"task-create-tool-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreatingATaskRecordsTheBriefAndBindsItToTheConversationDevice()
    {
        var fixture = TaskCreationFixture.Create(root);

        var result = await fixture.InvokeAsync("""
            {
              "title": "Door 202 opens without the safety gate closed",
              "type": "Issue",
              "intent": "Prevent the door from opening while the gate is open",
              "expectedResult": "Door 202 only opens when the safety gate reports closed",
              "background": "Operator reported an intermittent door opening at line start.",
              "evidence": "FB 202 \"VocDoorGeneral\" network 3 sets DoorOpen from StartButton alone; the SafetyGateClosed contact is not in that network.",
              "proposedSolutions": "Add the SafetyGateClosed contact to the network 3 rung, or interlock the output in a separate safety network."
            }
            """);

        var task = fixture.ReadTask(ResultTaskId(result));
        Assert.Equal("Door 202 opens without the safety gate closed", task.Title);
        Assert.Equal(GraphTaskType.Issue, task.Type);
        Assert.Equal(GraphTaskScopeKind.Worktree, task.ScopeKind);
        Assert.Equal(TaskCreationFixture.WorktreeId, task.WorktreeId);
        Assert.Equal(TaskCreationFixture.DeviceId, task.DeviceId); // the conversation's device, not an argument
        Assert.Equal(GraphTaskStatus.Todo, task.Status);
        Assert.Equal("Prevent the door from opening while the gate is open", task.Intent);
        Assert.Equal("Door 202 only opens when the safety gate reports closed", task.ExpectedResult);

        var description = task.Description ?? string.Empty;
        Assert.Contains("## Background", description, StringComparison.Ordinal);
        Assert.Contains("Operator reported an intermittent door opening at line start.", description, StringComparison.Ordinal);
        Assert.Contains("## Evidence", description, StringComparison.Ordinal);
        Assert.Contains("SafetyGateClosed contact is not in that network", description, StringComparison.Ordinal);
        Assert.Contains("## Proposed solutions", description, StringComparison.Ordinal);
        Assert.Contains("interlock the output in a separate safety network", description, StringComparison.Ordinal);
        // Section order is the brief's reading order, so the recorded task can be read top to bottom.
        Assert.True(
            description.IndexOf("## Background", StringComparison.Ordinal)
                < description.IndexOf("## Evidence", StringComparison.Ordinal)
            && description.IndexOf("## Evidence", StringComparison.Ordinal)
                < description.IndexOf("## Proposed solutions", StringComparison.Ordinal));

        // The result names the task it created so the agent reports the real identity, not a guessed one.
        Assert.Equal(task.Title, result.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ATaskWithoutABriefSectionKeepsItsDescriptionEmpty()
    {
        var fixture = TaskCreationFixture.Create(root);

        var result = await fixture.InvokeAsync("""
            {"title": "Rename the grayscale block", "type": "Improvement",
             "intent": "Match the naming convention", "expectedResult": "Block renamed and validated"}
            """);

        var task = fixture.ReadTask(ResultTaskId(result));
        // No "## " scaffolding for sections the conversation had nothing for: the task page must not
        // show empty headings a reader would read as missing evidence.
        Assert.True(string.IsNullOrEmpty(task.Description));
    }

    [Fact]
    public void TheSchemaRefusesInvalidTaskTypesAndEmptyRequiredFieldsBeforeTheApprovalCard()
    {
        var schema = TaskCreationTool.InputSchema;
        var properties = schema.GetProperty("properties");
        var declared = properties.GetProperty("type").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()).ToArray();

        // A live turn sent type "bug": the tool then refused it only after the user had approved the
        // card, so the turn reported a failure on a card the user had just accepted. The agent loop
        // validates every call against this schema before it dispatches it, so the allowed values have
        // to be declared here and not only in this class's parser.
        Assert.Equal(new[] { "Issue", "Improvement", "Feature" }, declared);

        // The same applies to an empty required value, which the parser's non-blank check would
        // otherwise refuse on the far side of the card.
        foreach (var name in new[] { "title", "intent", "expectedResult" })
        {
            Assert.Equal(1, properties.GetProperty(name).GetProperty("minLength").GetInt32());
        }
    }

    [Fact]
    public async Task ARetryAfterAnApprovedCallFailedRaisesItsOwnCardInsteadOfInheritingTheDecision()
    {
        var fixture = TaskCreationFixture.Create(root);
        var pending = new PendingToolActions();
        var cards = new List<string>();
        var sandbox = new AgentSandbox(new SandboxPolicy(), 20, _ =>
        {
            var completion = new TaskCompletionSource<ToolConfirmation>(TaskCreationOptions.RunContinuationsAsynchronously);
            var id = pending.Add("ctx", "requester", (decision, _) =>
            {
                completion.TrySetResult(decision);
                return Task.FromResult<object?>(null);
            });
            cards.Add(id);
            return completion.Task;
        });
        // A whitespace-only goal passes the schema's presence check, so the tool is what refuses it —
        // the live sequence that raised the question of whether the agent's retry could be auto-denied.
        const string blank = """{"title":"Something","type":"Issue","intent":"   ","expectedResult":"result"}""";
        const string corrected = """{"title":"Add a guard","type":"Issue","intent":"goal","expectedResult":"result"}""";

        var first = sandbox.CheckAsync(new ChatToolCall("call-1", TaskCreationTool.ToolName, blank));
        Assert.Single(cards);
        await pending.ResolveAsync(cards[0], ToolConfirmation.AllowOnce, "ctx", "requester");
        Assert.Null(await first);
        var error = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeAsync(blank));
        Assert.Equal("TOOL_ARGUMENT_INVALID", error.Code);

        var retry = sandbox.CheckAsync(new ChatToolCall("call-2", TaskCreationTool.ToolName, corrected));
        // The retry raises its own card and waits for it: the spent decision is not inherited, and
        // nothing auto-denies it.
        Assert.Equal(2, cards.Count);
        Assert.False(retry.IsCompleted);
        await pending.ResolveAsync(cards[1], ToolConfirmation.AllowOnce, "ctx", "requester");
        Assert.Null(await retry);

        await fixture.InvokeAsync(corrected);
        var tasks = fixture.ReadTasks();
        Assert.Equal(2, tasks.Count); // the fixture's own task plus the corrected creation
        Assert.Contains(tasks, task => task.Title == "Add a guard");
    }

    [Fact]
    public async Task AnUnknownTaskTypeIsRefusedBeforeAnythingIsCreated()
    {
        var fixture = TaskCreationFixture.Create(root);

        var error = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeAsync("""
            {"title": "Something", "type": "Bug", "intent": "goal", "expectedResult": "result"}
            """));

        Assert.Equal("TASK_TYPE_INVALID", error.Code);
        Assert.Contains("Issue", error.Message, StringComparison.Ordinal);
        Assert.Single(fixture.ReadTasks()); // only the fixture's own pre-existing task
    }

    [Fact]
    public async Task AWhitespaceOnlyRequiredFieldIsRefusedBeforeAnythingIsCreated()
    {
        var fixture = TaskCreationFixture.Create(root);

        // The loop's schema check only requires presence, so the tool itself refuses an empty brief
        // rather than persisting a task whose goal is blank.
        var error = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeAsync("""
            {"title": "Something", "type": "Issue", "intent": "   ", "expectedResult": "result"}
            """));

        Assert.Equal("TOOL_ARGUMENT_INVALID", error.Code);
        Assert.Contains("intent", error.Message, StringComparison.Ordinal);
        Assert.Single(fixture.ReadTasks());
    }

    [Fact]
    public async Task RejectingTheCallOnTheApprovalCardCreatesNoTask()
    {
        var fixture = TaskCreationFixture.Create(root);
        var arguments = """
            {"title": "Add a guard", "type": "Issue", "intent": "goal", "expectedResult": "result",
             "evidence": "Network 3 has no interlock."}
            """;
        var sandbox = new AgentSandbox(new SandboxPolicy(), 20, _ => Task.FromResult(ToolConfirmation.Deny));

        var verdict = await sandbox.CheckAsync(new ChatToolCall("call-task", TaskCreationTool.ToolName, arguments));

        Assert.NotNull(verdict);
        Assert.Contains("SANDBOX_USER_DENIED", verdict!.ErrorJson, StringComparison.Ordinal);
        // The loop never dispatches a refused call, so the worktree still holds only its own task.
        Assert.Single(fixture.ReadTasks());
    }

    [Fact]
    public async Task TheApprovalCardCarriesTheFullBriefSoTheUserApprovesWhatIsRecorded()
    {
        var arguments = """
            {"title": "Add a guard", "type": "Issue", "intent": "goal", "expectedResult": "result",
             "background": "A long background the auditor needs to read before approving.",
             "evidence": "Network 3 has no interlock.", "proposedSolutions": "Interlock the output."}
            """;
        ToolConfirmationRequest? shown = null;
        var sandbox = new AgentSandbox(new SandboxPolicy(), 20, request =>
        {
            shown = request;
            return Task.FromResult(ToolConfirmation.Deny);
        });

        // The tool is classified destructive, which is what routes it into the confirmation path.
        Assert.Equal(SandboxTier.Destructive, new SandboxPolicy().Classify(TaskCreationTool.ToolName));
        await sandbox.CheckAsync(new ChatToolCall("call-card", TaskCreationTool.ToolName, arguments));

        Assert.NotNull(shown);
        // Full arguments, not the 160-character audit summary: the card is where the user reads the
        // brief that becomes the task.
        Assert.Equal(arguments.Trim(), shown!.ArgumentsSummary);
    }

    [Fact]
    public async Task WithoutADeviceTheToolReportsTheMissingContext()
    {
        var fixture = TaskCreationFixture.Create(root);

        var error = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeWithoutDeviceAsync("""
            {"title": "Something", "type": "Issue", "intent": "goal", "expectedResult": "result"}
            """));

        Assert.Equal("DEVICE_SELECTION_REQUIRED", error.Code);
        Assert.Single(fixture.ReadTasks());
    }

    [Fact]
    public async Task CreatingATaskRelatesTheConversationThatCalledItWithAutoProvenance()
    {
        var fixture = TaskCreationFixture.Create(root, sessionId: "session-1");

        var result = await fixture.InvokeAsync("""
            {"title": "Add a guard", "type": "Issue", "intent": "goal", "expectedResult": "result"}
            """);

        // The conversation created this task by doing the work, so the relation is `auto` and, with no
        // primary present, it becomes the primary (ADR-0014 Decision 2, AC-010).
        var relation = Assert.Single(fixture.ReadRelations());
        Assert.Equal(ResultTaskId(result), relation.TaskId);
        Assert.Equal(GraphProvenance.Auto, relation.Provenance);
        Assert.True(relation.IsPrimary);
        Assert.Null(result.GetProperty("relationWarning").GetString());
    }

    [Fact]
    public async Task CreatingATaskAddsARelationWithoutMovingAnExistingPrimary()
    {
        var fixture = TaskCreationFixture.Create(root, sessionId: "session-1");
        fixture.Relate(TaskCreationFixture.ExistingTaskId);

        var result = await fixture.InvokeAsync("""
            {"title": "Add a guard", "type": "Issue", "intent": "goal", "expectedResult": "result"}
            """);

        var relations = fixture.ReadRelations();
        Assert.Equal(2, relations.Count);
        var added = Assert.Single(relations, relation => relation.TaskId == ResultTaskId(result));
        Assert.Equal(GraphProvenance.Auto, added.Provenance);
        // Automatic association never moves the primary.
        Assert.False(added.IsPrimary);
        Assert.True(Assert.Single(relations, relation => relation.TaskId == TaskCreationFixture.ExistingTaskId).IsPrimary);
    }

    [Fact]
    public async Task AFailedRelationWriteStillReportsTheCreatedTaskWithAWarning()
    {
        var fixture = TaskCreationFixture.Create(root, sessionId: "session-1");
        var previous = SessionGraphOperations.AutomaticRelationOverride;
        SessionGraphOperations.AutomaticRelationOverride = (_, _, _) =>
            throw new InvalidOperationException("injected relation failure.");
        JsonElement result;
        try
        {
            result = await fixture.InvokeAsync("""
                {"title": "Add a guard", "type": "Issue", "intent": "goal", "expectedResult": "result"}
                """);
        }
        finally { SessionGraphOperations.AutomaticRelationOverride = previous; }

        // The user approved a creation: the task exists and is reported, and the failure is named
        // rather than thrown, because failing the call would hide a task that is really there.
        var task = fixture.ReadTask(ResultTaskId(result));
        Assert.Equal("Add a guard", task.Title);
        Assert.Contains("injected relation failure", result.GetProperty("relationWarning").GetString()!, StringComparison.Ordinal);
        Assert.Empty(fixture.ReadRelations());
    }

    [Fact]
    public async Task TheRelationFollowsTheConversationsLiveIdentityRatherThanACapturedOne()
    {
        var fixture = TaskCreationFixture.Create(root);
        var live = "session-a";
        var spec = fixture.SpecFor(() => live);

        var first = await fixture.InvokeAsync(spec, """
            {"title": "First finding", "type": "Issue", "intent": "goal", "expectedResult": "result"}
            """);
        // The device chat swaps its conversation in place while the loop and this catalog are reused,
        // so the identity must be resolved at call time or the relation lands on the previous chat.
        live = "session-b";
        var second = await fixture.InvokeAsync(spec, """
            {"title": "Second finding", "type": "Issue", "intent": "goal", "expectedResult": "result"}
            """);

        Assert.Equal(ResultTaskId(first), Assert.Single(fixture.ReadRelations("session-a")).TaskId);
        Assert.Equal(ResultTaskId(second), Assert.Single(fixture.ReadRelations("session-b")).TaskId);
    }

    [Fact]
    public async Task WithoutAConversationIdentityNoRelationIsWritten()
    {
        // The Workbench Assistant's own conversation is out of scope (ADR-0014 Decision 3): a call with
        // no device-chat conversation creates the task and relates nothing.
        var fixture = TaskCreationFixture.Create(root);

        var result = await fixture.InvokeAsync("""
            {"title": "Assistant finding", "type": "Issue", "intent": "goal", "expectedResult": "result"}
            """);

        Assert.Equal("Assistant finding", fixture.ReadTask(ResultTaskId(result)).Title);
        Assert.Null(result.GetProperty("relationWarning").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ResultTaskId(JsonElement result) => result.GetProperty("taskId").GetString()!;

    /// <summary>One workbench worktree with a registered device and one pre-existing task, wired to the
    /// real tool, graph factory and worktree task store.</summary>
    private sealed class TaskCreationFixture
    {
        public const string WorktreeId = "master-1";
        public const string DeviceId = "device-1";
        public const string ExistingTaskId = "task-existing";

        private readonly string workbenchRoot;
        private readonly string workbenchId;
        private readonly AtomicJsonStore store;
        private readonly WorkbenchApiState state;

        private TaskCreationFixture(
            string workbenchRoot, string workbenchId, AtomicJsonStore store, WorkbenchApiState state,
            DeviceContext device, AgentToolSpec spec, string? sessionId)
        {
            this.workbenchRoot = workbenchRoot;
            this.workbenchId = workbenchId;
            this.store = store;
            this.state = state;
            Device = device;
            Spec = spec;
            SessionId = sessionId;
        }

        public DeviceContext Device { get; }
        public AgentToolSpec Spec { get; }

        /// <summary>The conversation the catalog was built for; null is the assistant's own case, which
        /// has no device-chat conversation to relate.</summary>
        public string? SessionId { get; }

        public static TaskCreationFixture Create(string parent, string? sessionId = null)
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

            using (var graphStore = new EngineeringGraphStore(workbench.RootPath))
            {
                var graph = new EngineeringGraphService(graphStore, workbench.WorkbenchId, id => id == WorktreeId);
                graph.CreateTask(ExistingTaskId, GraphTaskScopeKind.Worktree, WorktreeId, "Existing work",
                    GraphTaskType.Feature, intent: "intent", expectedResult: "result", deviceId: DeviceId);
            }

            var state = new WorkbenchApiState(catalog, store);
            state.Add(workbench);
            var tool = new TaskCreationTool(state, new EngineeringGraphApiFactory(), new WorktreeTaskStore(store));
            return new TaskCreationFixture(
                workbench.RootPath, workbench.WorkbenchId, store, state, device,
                TaskCreationTool.CreateSpec(tool, () => device, () => sessionId), sessionId);
        }

        /// <summary>Relates the conversation to a task directly, so a call can be made from a
        /// conversation that already has a primary relation.</summary>
        public void Relate(string taskId, GraphProvenance provenance = GraphProvenance.Manual, bool primary = true)
        {
            using var graphStore = new EngineeringGraphStore(workbenchRoot);
            var graph = new EngineeringGraphService(graphStore, workbenchId, id => id == WorktreeId);
            graph.RegisterEntity(new GraphEntity(GraphEntityKind.Session, SessionId!, workbenchId, WorktreeId, DeviceId));
            graph.AddSessionTask(SessionId!, taskId, provenance, makePrimaryIfNone: primary);
        }

        public IReadOnlyList<SessionTaskRelation> ReadRelations() => ReadRelations(SessionId!);

        public IReadOnlyList<SessionTaskRelation> ReadRelations(string sessionId)
        {
            using var graphStore = new EngineeringGraphStore(workbenchRoot);
            return new EngineeringGraphService(graphStore, workbenchId, id => id == WorktreeId)
                .ListSessionTaskRelations([sessionId]);
        }

        /// <summary>The same tool with a different conversation-identity provider, so a call's live
        /// resolution can be exercised the way the device chat's reused catalog does.</summary>
        public AgentToolSpec SpecFor(Func<string?> sessionId) =>
            TaskCreationTool.CreateSpec(
                new TaskCreationTool(state, new EngineeringGraphApiFactory(), new WorktreeTaskStore(store)),
                () => Device,
                sessionId);

        public async Task<JsonElement> InvokeAsync(string argumentsJson)
        {
            using var arguments = JsonDocument.Parse(argumentsJson);
            return await Spec.Caller.CallAsync<JsonElement>(
                TaskCreationTool.ToolName, arguments.RootElement, CancellationToken.None);
        }

        public async Task<JsonElement> InvokeAsync(AgentToolSpec spec, string argumentsJson)
        {
            using var arguments = JsonDocument.Parse(argumentsJson);
            return await spec.Caller.CallAsync<JsonElement>(
                TaskCreationTool.ToolName, arguments.RootElement, CancellationToken.None);
        }

        /// <summary>The same call with no selected device, which is the chat's own precondition.</summary>
        public async Task<JsonElement> InvokeWithoutDeviceAsync(string argumentsJson)
        {
            var tool = new TaskCreationTool(state, new EngineeringGraphApiFactory(), new WorktreeTaskStore(store));
            var spec = TaskCreationTool.CreateSpec(tool, () => null, () => null);
            using var arguments = JsonDocument.Parse(argumentsJson);
            return await spec.Caller.CallAsync<JsonElement>(
                TaskCreationTool.ToolName, arguments.RootElement, CancellationToken.None);
        }

        public IReadOnlyList<GraphTask> ReadTasks()
        {
            using var graphStore = new EngineeringGraphStore(workbenchRoot);
            return new EngineeringGraphService(graphStore, workbenchId, id => id == WorktreeId).ListTasks(WorktreeId);
        }

        public GraphTask ReadTask(string taskId)
        {
            using var graphStore = new EngineeringGraphStore(workbenchRoot);
            return new EngineeringGraphService(graphStore, workbenchId, id => id == WorktreeId).FindTask(taskId)!;
        }
    }
}
