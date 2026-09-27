using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent.Workbench;
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

    private static WebApplicationFactory<Program> FactoryWithModel() => Factory().WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeepSeek:ApiKey"] = "test-key",
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new AssistantModelClientFactory());
        });
    });

    private static WebApplicationFactory<Program> FactoryWithWorkbench(
        string name,
        string rootPrefix,
        out WorkbenchApiState state,
        out string workbenchId,
        bool withModel = false)
    {
        var factory = withModel ? FactoryWithModel() : Factory();
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

    private sealed class AssistantModelClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new AssistantModelHandler());
    }

    private sealed class AssistantModelHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"I can help choose a project.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"),
            });
    }
}
