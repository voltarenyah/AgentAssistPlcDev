using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Contracts.Engineering;
using Contracts.Sandbox;
using System.Text.Json;
using Xunit;

/// <summary>
/// The device chat's <c>open_tia_project</c> action: the user asks the knowledge agent to open the
/// worktree it is talking about, and TIA Portal comes up with that worktree's registered project.
///
/// The attach/open behaviour itself is covered by
/// <c>Agent.Tests.WorkbenchCoordinatorTests.ShowWorktreeProjectInTia*</c>; these tests cover the
/// action's agent-facing contract: the tier that decides whether an approval card interrupts the
/// request, the no-argument binding to the conversation's own device, and the reported result.
/// </summary>
public sealed class OpenTiaProjectToolTests : IDisposable
{
    private const string ProjectPath = @"C:\Projects\Line.ap17";

    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"open-tia-tool-{Guid.NewGuid():N}");

    [Fact]
    public void ToolIsWriteTierSoNoApprovalCardInterruptsTheRequest()
    {
        Assert.Equal(SandboxTier.Write, new SandboxPolicy().Classify(OpenTiaProjectTool.ToolName));
    }

    [Fact]
    public void ToolTakesNoArgumentsTheModelCouldAimAtAnotherWorktree()
    {
        var schema = OpenTiaProjectTool.InputSchema;

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public void DeviceChatExposesTheTiaOpenActionToTheModel()
    {
        var fixture = CreateFixture();
        var catalog = ApiChatService.BuildToolCatalog(
            new McpToolCatalog([]),
            new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { })),
            fixture.Device,
            fixture.State,
            new EngineeringGraphApiFactory(),
            new ActiveTaskContextService(),
            new WorktreeTaskStore(fixture.Store),
            fixture.Coordinator);

        var spec = catalog.Resolve(OpenTiaProjectTool.ToolName);

        Assert.Equal("workbench", spec.ServerName);
        Assert.Contains("TIA Portal", spec.Description, StringComparison.Ordinal);
        // The action is exposed alongside the device chat's other in-process tools.
        Assert.NotNull(catalog.Tools.SingleOrDefault(item => item.Name == TaskSourceObjectListTool.ToolName));
        Assert.NotNull(catalog.Tools.SingleOrDefault(item => item.Name == TaskCreationTool.ToolName));
    }

    [Fact]
    public async Task CallerRequiresTheConversationsDevice()
    {
        var fixture = CreateFixture();
        var spec = OpenTiaProjectTool.CreateSpec(fixture.Tool, () => null);

        var error = await Assert.ThrowsAsync<ToolCallException>(() =>
            spec.Caller.CallAsync<JsonElement>(
                OpenTiaProjectTool.ToolName, new { }, CancellationToken.None));

        Assert.Equal("DEVICE_SELECTION_REQUIRED", error.Code);
    }

    [Fact]
    public async Task CallerOpensTheWorktreesRegisteredProjectWithUi()
    {
        var fixture = CreateFixture();

        var result = await fixture.Spec.Caller.CallAsync<JsonElement>(
            OpenTiaProjectTool.ToolName, new { }, CancellationToken.None);

        Assert.True(result.GetProperty("opened").GetBoolean());
        Assert.Equal("Line", result.GetProperty("projectName").GetString());
        Assert.Equal(ProjectPath, result.GetProperty("projectPath").GetString());
        Assert.True(result.GetProperty("withUI").GetBoolean());
        Assert.False(result.GetProperty("reusedRunningSession").GetBoolean());
        Assert.Equal(
            ["get_project_info", "list_sessions", "connect", "get_project_info"],
            fixture.Engineering.Calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Fixture(
        OpenTiaProjectTool Tool,
        AgentToolSpec Spec,
        QueuedCaller Engineering,
        DeviceContext Device,
        WorkbenchApiState State,
        AtomicJsonStore Store,
        WorkbenchCoordinator Coordinator);

    private Fixture CreateFixture()
    {
        const string worktreeId = "wt-1";
        const string deviceId = "dev-1";
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, Guid.NewGuid().ToString("N")));
        var workbench = catalog.RegisterWorktree(
            catalog.Create("Line", null),
            new WorkbenchWorktreeRegistration(worktreeId, "master", "master", "master"));
        var device = WorkbenchPaths.ResolveDevice(
            workbench.WorkbenchId, workbench.RootPath, worktreeId, "master", deviceId, "PLC_1");
        Directory.CreateDirectory(device.SourceRoot);
        store.Write(Path.Combine(device.WorktreeRoot, "worktree.json"), new WorktreeMetadata(
            "1.2", worktreeId, workbench.WorkbenchId, "master", "master",
            DateTimeOffset.UtcNow.ToString("O"), "head-1", "project-1", ProjectPath, [deviceId], null));
        store.Write(Path.Combine(device.DeviceRoot, "device.json"), new DeviceMetadata(
            "1.2", deviceId, worktreeId, "PLC_1", "project-1", null, null, null,
            new KnowledgeState(false, new Dictionary<string, string>(), null), []));

        var engineering = new QueuedCaller()
            .Fail("get_project_info", "NOT_CONNECTED", "No project connected. Call connect first.")
            .Respond("list_sessions", Array.Empty<SessionInfo>())
            .Respond("connect", new { connected = true })
            .Respond("get_project_info", new ProjectInfo { Name = "Line", Path = ProjectPath });
        var coordinator = new WorkbenchCoordinator(
            engineering, new QueuedCaller(), new QueuedCaller(), catalog, store,
            new DeviceReconciler(), new DeviceSourceResolver(_ => { }));
        var state = new WorkbenchApiState(catalog, store);
        state.Add(workbench);

        var tool = new OpenTiaProjectTool(state, coordinator);
        return new Fixture(
            tool,
            OpenTiaProjectTool.CreateSpec(tool, () => device),
            engineering,
            device,
            state,
            store,
            coordinator);
    }

    /// <summary>Queued scripted caller: one response per call, every call recorded.</summary>
    private sealed class QueuedCaller : IMcpToolCaller
    {
        private readonly Dictionary<string, Queue<Func<object, object>>> scripts =
            new(StringComparer.Ordinal);

        public List<string> Calls { get; } = new();

        public QueuedCaller Respond<T>(string tool, T response) where T : notnull
        {
            Queue(tool).Enqueue(_ => response);
            return this;
        }

        public QueuedCaller Fail(string tool, string code, string message)
        {
            Queue(tool).Enqueue(_ => throw new ToolCallException(code, message, null));
            return this;
        }

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (!scripts.TryGetValue(tool, out var queue) || queue.Count == 0)
            {
                throw new InvalidOperationException($"QueuedCaller: no scripted response for '{tool}'.");
            }

            return Task.FromResult((T)queue.Dequeue()(args));
        }

        private Queue<Func<object, object>> Queue(string tool)
        {
            if (!scripts.TryGetValue(tool, out var queue))
            {
                queue = new Queue<Func<object, object>>();
                scripts[tool] = queue;
            }

            return queue;
        }
    }
}
