using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>
/// The Workbench Assistant panel's HTTP surface is served in-process by
/// <see cref="WorkbenchAssistantService"/> over the shared AgentLoop (ADR-0005). These tests cover
/// the panel's wire contract: SSE-framed events on success, and a failure reported *inside* the
/// event stream rather than as an HTTP error, because the panel parses the body before it can
/// surface anything.
/// </summary>
public sealed class AppAssistantChatEndpointTests
{
    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));

    private static WebApplicationFactory<Program> FactoryWithModel(AssistantModelHandler? model = null) => Factory().WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeepSeek:ApiKey"] = "test-key",
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new AssistantModelClientFactory(model ?? new AssistantModelHandler()));
        });
    });

    private static WebApplicationFactory<Program> FactoryWithWorkbench(
        string name,
        string rootPrefix,
        out WorkbenchApiState state,
        out string workbenchId,
        bool withModel = false,
        AssistantModelHandler? model = null)
    {
        var factory = withModel ? FactoryWithModel(model) : Factory();
        state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var root = Path.Combine(Path.GetTempPath(), rootPrefix + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create(name, root);
        state.Open(root);
        state.Select(workbench.WorkbenchId);
        workbenchId = workbench.WorkbenchId;
        return factory;
    }

    [Fact]
    public async Task BootstrapWorksWithoutASelectedWorkbench()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/app-assistant/bootstrap", new { message = string.Empty });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: answer", body);
        using var document = JsonDocument.Parse(DataLine(body, "state"));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("runtimeSnapshot").ValueKind);
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("workbenches").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("sessionId").GetString()));
    }

    [Fact]
    public async Task BootstrapDescribesTheSelectedWorkbenchWithoutADevice()
    {
        await using var factory = FactoryWithWorkbench("Assistant Bootstrap", "assistant-bootstrap-", out _, out var workbenchId);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/app-assistant/bootstrap",
            new { message = string.Empty });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        // Context is served without a device so the panel can render and chat while the user
        // is still choosing one.
        Assert.Contains("event: progress", body);
        Assert.Contains("event: state", body);
        Assert.Contains("event: answer", body);
        Assert.Contains("Assistant Bootstrap", body);

        // The panel's normalizer reads camelCase, so assert the shape inside the frame and not
        // merely that a frame exists: a PascalCase payload leaves the panel with no runtime
        // context at all, rendering no worktrees and no revision without failing anything.
        using var document = JsonDocument.Parse(DataLine(body, "state"));
        var snapshot = document.RootElement.GetProperty("runtimeSnapshot");
        Assert.Equal(workbenchId, snapshot.GetProperty("workbenchId").GetString());
        Assert.Equal("Assistant Bootstrap", snapshot.GetProperty("name").GetString());
        // The casing is the point: with PascalCase these lookups throw and the panel receives no
        // runtime context at all. Whether the fixture workbench has worktrees is environment-
        // dependent, so assert the shape rather than a count.
        Assert.Equal(JsonValueKind.Array, snapshot.GetProperty("runtime").GetProperty("worktrees").ValueKind);
        Assert.Equal(JsonValueKind.Array, snapshot.GetProperty("availableActions").ValueKind);
    }

    /// <summary>The JSON payload of the first frame with the given event name.</summary>
    private static string DataLine(string body, string eventName)
    {
        var lines = body.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].Trim() == $"event: {eventName}")
            {
                for (var next = index + 1; next < lines.Length; next++)
                {
                    if (lines[next].StartsWith("data: ", StringComparison.Ordinal))
                        return lines[next]["data: ".Length..];
                }
            }
        }
        throw new InvalidOperationException($"No '{eventName}' frame in: {body}");
    }

    [Fact]
    public async Task ChatWithoutASelectedDeviceUsesTheAssistantConversation()
    {
        await using var factory = FactoryWithModel();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/app-assistant/chat",
            new { message = "What should I do next?" });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(!body.Contains("event: error", StringComparison.Ordinal), body);
        Assert.Contains("event: answer", body);
        Assert.Contains("I can help choose a project.", body);
    }

    [Fact]
    public async Task SameConversationAnswersWithProjectAndWorktreeSelectedButNoDevice()
    {
        await using var factory = FactoryWithWorkbench("Assistant Context", "assistant-context-",
            out var state, out var workbenchId, withModel: true);
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var store = factory.Services.GetRequiredService<AtomicJsonStore>();
        const string worktreeId = "wt-1";
        var workbench = catalog.RegisterWorktree(state.Workbench(workbenchId),
            new WorkbenchWorktreeRegistration(worktreeId, "master", "master", "master"));
        var worktreeRoot = Path.Combine(workbench.RootPath, "worktrees", "master");
        store.Write(Path.Combine(worktreeRoot, "worktree.json"), new WorktreeMetadata(
            "1.0", worktreeId, workbenchId, "master", "master",
            DateTimeOffset.UtcNow.ToString("O"), null, null, null, [], null));
        state.Refresh(workbenchId);

        var first = await client.PostAsJsonAsync("/api/app-assistant/chat", new { message = "Help me choose a worktree." });
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.DoesNotContain("event: error", firstBody);
        using var firstState = JsonDocument.Parse(DataLine(firstBody, "state"));
        var sessionId = firstState.RootElement.GetProperty("sessionId").GetString();

        state.Select(workbenchId, worktreeId);
        var second = await client.PostAsJsonAsync("/api/app-assistant/chat", new { message = "What can I do here?" });
        var secondBody = await second.Content.ReadAsStringAsync();
        Assert.DoesNotContain("event: error", secondBody);
        Assert.Contains("I can help choose a project.", secondBody);
        using var secondState = JsonDocument.Parse(DataLine(secondBody, "state"));
        Assert.Equal(sessionId, secondState.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal(worktreeId, secondState.RootElement.GetProperty("runtimeSnapshot")
            .GetProperty("runtime").GetProperty("focus").GetProperty("worktreeId").GetString());
    }

    [Fact]
    public async Task AssistantReadsGraphTasksBeforeSuggestingAnotherTask()
    {
        const string worktreeId = "wt-1";
        var model = new AssistantModelHandler("assistant_list_tasks");
        await using var factory = FactoryWithWorkbench("Task Guidance", "assistant-tasks-",
            out var state, out var workbenchId, withModel: true, model);
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var store = factory.Services.GetRequiredService<AtomicJsonStore>();
        var workbench = catalog.RegisterWorktree(state.Workbench(workbenchId),
            new WorkbenchWorktreeRegistration(worktreeId, "master", "master", "master"));
        var worktreeRoot = Path.Combine(workbench.RootPath, "worktrees", "master");
        store.Write(Path.Combine(worktreeRoot, "worktree.json"), new WorktreeMetadata(
            "1.0", worktreeId, workbenchId, "master", "master",
            DateTimeOffset.UtcNow.ToString("O"), null, null, null, [], null));
        state.Refresh(workbenchId);
        using (var graph = factory.Services.GetRequiredService<EngineeringGraphApiFactory>().Open(state.Workbench(workbenchId)))
            graph.Service.CreateTask("task-1", GraphTaskScopeKind.Project, null,
                "Inspect startup sequence", GraphTaskType.Feature,
                intent: "Understand startup", expectedResult: "Expected control sequence documented");
        model.Arguments = JsonSerializer.Serialize(new { workbenchId, worktreeId });

        var response = await client.PostAsJsonAsync("/api/app-assistant/chat",
            new { message = "Should I create another startup task?" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("event: error", body);
        Assert.Equal(2, model.RequestBodies.Count);
        Assert.Contains("assistant_list_tasks", model.RequestBodies[0]);
        Assert.Contains("Inspect startup sequence", model.RequestBodies[1]);
        Assert.Contains("Expected control sequence documented", model.RequestBodies[1]);
    }

    [Fact]
    public async Task AssistantCanPresentAChoiceInThePanel()
    {
        var model = new AssistantModelHandler("assistant_present_choices")
        {
            Arguments = JsonSerializer.Serialize(new
            {
                question = "Which project should I use?",
                options = new[] { new { value = "wb-1", label = "Line A", description = "Existing workbench" } },
            }),
        };
        await using var factory = FactoryWithModel(model);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/app-assistant/chat", new { message = "Choose a project" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("event: error", body);
        using var frame = JsonDocument.Parse(DataLine(body, "state"));
        var decision = frame.RootElement.GetProperty("decision");
        Assert.Equal("Which project should I use?", decision.GetProperty("question").GetString());
        Assert.Equal("wb-1", decision.GetProperty("options")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task AssistantCanSelectAnExistingWorkbenchWithoutDeviceSelection()
    {
        var model = new AssistantModelHandler("assistant_select_scope");
        await using var factory = FactoryWithWorkbench("Selection Fixture", "assistant-select-",
            out var state, out var workbenchId, withModel: true, model);
        var other = factory.Services.GetRequiredService<WorkbenchCatalog>().Create("Other Selection",
            Path.Combine(Path.GetTempPath(), "assistant-other-" + Guid.NewGuid().ToString("N")));
        state.Open(other.RootPath);
        state.Select(other.WorkbenchId);
        model.Arguments = JsonSerializer.Serialize(new { workbenchId });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/app-assistant/chat", new { message = "Focus this workbench" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("event: error", body);
        using var frame = JsonDocument.Parse(DataLine(body, "state"));
        Assert.Equal("selection", frame.RootElement.GetProperty("change").GetProperty("kind").GetString());
        Assert.Equal(workbenchId, state.Selection?.WorkbenchId);
    }

    [Fact]
    public async Task AssistantCreatesADeviceTaskOnlyAfterApproval()
    {
        const string worktreeId = "wt-1";
        const string deviceId = "dev-1";
        var model = new AssistantModelHandler("assistant_create_task");
        await using var factory = FactoryWithWorkbench("Task Creation", "assistant-create-task-",
            out var state, out var workbenchId, withModel: true, model);
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var store = factory.Services.GetRequiredService<AtomicJsonStore>();
        var workbench = catalog.RegisterWorktree(state.Workbench(workbenchId),
            new WorkbenchWorktreeRegistration(worktreeId, "master", "master", "master"));
        var worktreeRoot = Path.Combine(workbench.RootPath, "worktrees", "master");
        var deviceRoot = Path.Combine(worktreeRoot, "devices", "PLC_1");
        Directory.CreateDirectory(deviceRoot);
        store.Write(Path.Combine(worktreeRoot, "worktree.json"), new WorktreeMetadata(
            "1.0", worktreeId, workbenchId, "master", "master",
            DateTimeOffset.UtcNow.ToString("O"), null, null, null, [deviceId], null));
        store.Write(Path.Combine(deviceRoot, "device.json"), new DeviceMetadata(
            "1.0", deviceId, worktreeId, "PLC_1", "PLC:1", null, null, null,
            new KnowledgeState(false, new Dictionary<string, string>(), null, false), []));
        state.Refresh(workbenchId);
        model.Arguments = JsonSerializer.Serialize(new
        {
            workbenchId, worktreeId, deviceId, title = "Inspect startup sequence", type = "Issue",
            intent = "Find the startup fault", expectedResult = "Cause documented",
        });

        var responseTask = client.PostAsJsonAsync("/api/app-assistant/chat", new { message = "Create the task" });
        var runtime = factory.Services.GetRequiredService<CompatibilityRuntimeState>();
        string? id = null;
        for (var attempt = 0; attempt < 100 && id is null; attempt++)
        {
            id = runtime.Logs.ToArray().Select(line => JsonDocument.Parse(line).RootElement)
                .Where(entry => entry.TryGetProperty("kind", out var kind) && kind.GetString() == "confirmation")
                .Select(entry => entry.GetProperty("id").GetString()).FirstOrDefault();
            if (id is null) await Task.Delay(20);
        }
        Assert.NotNull(id);
        Assert.Contains("Cause documented", runtime.Logs.ToArray()
            .Single(line => line.Contains($"\"id\":\"{id}\"", StringComparison.Ordinal)));
        using (var before = factory.Services.GetRequiredService<EngineeringGraphApiFactory>().Open(state.Workbench(workbenchId)))
            Assert.Empty(before.Service.ListTasks(worktreeId));

        var approval = await client.PostAsJsonAsync($"/api/chat/confirm/{id}", new { decision = "allowOnce" });
        Assert.Equal(HttpStatusCode.OK, approval.StatusCode);
        var response = await responseTask;
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("event: error", body);
        using var frame = JsonDocument.Parse(DataLine(body, "state"));
        Assert.Equal("task", frame.RootElement.GetProperty("change").GetProperty("kind").GetString());
        using var after = factory.Services.GetRequiredService<EngineeringGraphApiFactory>().Open(state.Workbench(workbenchId));
        var task = Assert.Single(after.Service.ListTasks(worktreeId));
        Assert.Equal(deviceId, task.DeviceId);
        Assert.Equal("Find the startup fault", task.Intent);
        Assert.Equal("Cause documented", task.ExpectedResult);
    }

    [Theory]
    [InlineData("assistant_create_workbench")]
    [InlineData("assistant_create_worktree")]
    public async Task ManagedCreationWaitsForApprovalBeforeChangingAnything(string toolName)
    {
        var model = new AssistantModelHandler(toolName);
        await using var factory = FactoryWithWorkbench("Approval Fixture", "assistant-approval-",
            out var state, out var workbenchId, withModel: true, model);
        using var client = factory.CreateClient();
        model.Arguments = toolName == "assistant_create_workbench"
            ? JsonSerializer.Serialize(new { name = "New Workbench", engineeringProjectPath = "C:/unused.ap17" })
            : JsonSerializer.Serialize(new { workbenchId, name = "feature", branch = "feature" });
        var initialCount = state.List().Count;
        var initialWorktrees = state.Workbench(workbenchId).Worktrees.Count;

        var responseTask = client.PostAsJsonAsync("/api/app-assistant/chat", new { message = "Create it" });
        var runtime = factory.Services.GetRequiredService<CompatibilityRuntimeState>();
        string? id = null;
        for (var attempt = 0; attempt < 100 && id is null; attempt++)
        {
            id = runtime.Logs.ToArray().Select(line => JsonDocument.Parse(line).RootElement)
                .Where(entry => entry.TryGetProperty("kind", out var kind) && kind.GetString() == "confirmation")
                .Select(entry => entry.GetProperty("id").GetString()).FirstOrDefault();
            if (id is null) await Task.Delay(20);
        }
        Assert.NotNull(id);
        Assert.Equal(initialCount, state.List().Count);
        Assert.Equal(initialWorktrees, state.Workbench(workbenchId).Worktrees.Count);
        var denial = await client.PostAsJsonAsync($"/api/chat/confirm/{id}", new { decision = "deny" });
        Assert.Equal(HttpStatusCode.OK, denial.StatusCode);
        var response = await responseTask;
        Assert.DoesNotContain("event: error", await response.Content.ReadAsStringAsync());
        var visibleLogs = await client.GetStringAsync("/api/logs");
        Assert.DoesNotContain(id, visibleLogs);
        Assert.Equal(initialCount, state.List().Count);
        Assert.Equal(initialWorktrees, state.Workbench(workbenchId).Worktrees.Count);
    }

    [Fact]
    public async Task BootstrapWithASelectedDeviceReturnsASessionIdBeforeTheFirstChatTurn()
    {
        await using var factory = FactoryWithWorkbench("Assistant Session", "assistant-session-", out var state, out var workbenchId);
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var store = factory.Services.GetRequiredService<AtomicJsonStore>();
        const string worktreeId = "wt-1";
        const string deviceId = "dev-1";
        var workbench = catalog.RegisterWorktree(
            state.Workbench(workbenchId),
            new WorkbenchWorktreeRegistration(worktreeId, "master", "master", "master"));
        var worktreeRoot = Path.Combine(workbench.RootPath, "worktrees", "master");
        var deviceRoot = Path.Combine(worktreeRoot, "devices", "PLC_1");
        Directory.CreateDirectory(deviceRoot);
        store.Write(Path.Combine(worktreeRoot, "worktree.json"), new WorktreeMetadata(
            "1.0", worktreeId, workbenchId, "master", "master",
            DateTimeOffset.UtcNow.ToString("O"), null, null, null, [deviceId], null));
        store.Write(Path.Combine(deviceRoot, "device.json"), new DeviceMetadata(
            "1.0", deviceId, worktreeId, "PLC_1", "PLC:1", null, null, null,
            new KnowledgeState(false, new Dictionary<string, string>(), null, false), []));
        state.Refresh(workbenchId);
        state.Select(workbenchId, worktreeId, deviceId);

        var response = await client.PostAsJsonAsync("/api/app-assistant/bootstrap", new { message = string.Empty });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(DataLine(body, "state"));
        var sessionId = document.RootElement.GetProperty("sessionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        var second = await client.PostAsJsonAsync("/api/app-assistant/bootstrap", new { message = string.Empty });
        using var secondDocument = JsonDocument.Parse(DataLine(await second.Content.ReadAsStringAsync(), "state"));
        Assert.Equal(sessionId, secondDocument.RootElement.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task HealthReportsTheInProcessService()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/app-assistant/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("in-process", body!["service"]?.ToString());
    }

    private sealed class AssistantModelClientFactory(AssistantModelHandler model) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(model, disposeHandler: false);
    }

    private sealed class AssistantModelHandler(string? tool = null) : HttpMessageHandler
    {
        public string Arguments { get; set; } = "{}";
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var response = tool is not null && RequestBodies.Count == 1
                ? "data: " + JsonSerializer.Serialize(new
                {
                    choices = new[] { new
                    {
                        index = 0,
                        delta = new { tool_calls = new[] { new
                        {
                            index = 0, id = "call_1", type = "function",
                            function = new { name = tool, arguments = Arguments },
                        } } },
                        finish_reason = (string?)null,
                    } },
                    usage = (object?)null,
                }) + "\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"\"},\"finish_reason\":\"tool_calls\"}],\"usage\":null}\n\ndata: [DONE]\n\n"
                : "data: {\"choices\":[{\"delta\":{\"content\":\"I can help choose a project.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            };
        }
    }
}
