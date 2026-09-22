using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent.Workbench;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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

    private static WebApplicationFactory<Program> FactoryWithWorkbench(
        string name,
        string rootPrefix,
        out WorkbenchApiState state,
        out string workbenchId)
    {
        var factory = Factory();
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
    public async Task ChatRejectsRequestsWithoutASelectedWorkbench()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/app-assistant/chat",
            new { message = "What should I do next?" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("WORKBENCH_SELECTION_REQUIRED", body!["error"]);
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
        // Context is served without a device so the panel can render the workbench while the user
        // is still choosing one; only a turn needs a device (ADR-0005's accepted non-goal).
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
    public async Task ChatWithoutASelectedDeviceReportsTheMissingSelectionInsideTheStream()
    {
        await using var factory = FactoryWithWorkbench("Assistant No Device", "assistant-nodevice-", out _, out _);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/app-assistant/chat",
            new { message = "What should I do next?" });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("event: error", body);
        Assert.Contains("DEVICE_SELECTION_REQUIRED", body);
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
}
