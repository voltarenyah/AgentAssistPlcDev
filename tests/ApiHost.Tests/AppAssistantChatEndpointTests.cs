using System.Net;
using System.Net.Http.Json;
using Agent.Workbench;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class AppAssistantChatEndpointTests
{
    [Fact]
    public async Task ChatRejectsRequestsWithoutASelectedWorkbench()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/app-assistant/chat",
            new { message = "What should I do next?" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("WORKBENCH_SELECTION_REQUIRED", body!["error"]);
    }

    [Fact]
    public async Task ChatKeepsAssistantFailureInsideTheAssistantEventStream()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("AppAssistant:ServiceUrl", "http://127.0.0.1:1");
            });
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var root = Path.Combine(Path.GetTempPath(), "assistant-chat-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Assistant Chat", root);
        state.Open(root);
        state.Select(workbench.WorkbenchId);

        var response = await client.PostAsJsonAsync(
            "/api/app-assistant/chat",
            new { message = "What should I do next?" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("event: progress", body);
        Assert.Contains("event: error", body);
        Assert.Contains("APP_ASSISTANT_UNAVAILABLE", body);
    }

    [Fact]
    public async Task BootstrapAllowsAnEmptyOrientationMessage()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("AppAssistant:ServiceUrl", "http://127.0.0.1:1");
            });
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var root = Path.Combine(Path.GetTempPath(), "assistant-bootstrap-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Assistant Bootstrap", root);
        state.Open(root);
        state.Select(workbench.WorkbenchId);

        var response = await client.PostAsJsonAsync(
            "/api/app-assistant/bootstrap",
            new { message = string.Empty });

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("event: progress", body);
        Assert.Contains("APP_ASSISTANT_UNAVAILABLE", body);
    }

    [Fact]
    public async Task FeedbackRejectsRequestsWithoutASelectedWorkbench()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/app-assistant/feedback",
            new { category = "successful_completion", runId = "run-1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("WORKBENCH_SELECTION_REQUIRED", body!["error"]);
    }

    [Fact]
    public async Task HealthReportsUnavailableWhenTheSidecarIsDown()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("AppAssistant:ServiceUrl", "http://127.0.0.1:1");
            });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/app-assistant/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("APP_ASSISTANT_UNAVAILABLE", body!["error"]);
    }
}
