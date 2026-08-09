using System.Net;
using System.Net.Http.Json;
using Agent.Workbench;
using ApiHost.AppAssistant;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class AppAssistantEndpointsTests
{
    [Fact]
    public async Task ContextEndpointReturnsScopedAssistantContext()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var root = Path.Combine(Path.GetTempPath(), "assistant-endpoint-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Assistant Endpoint", root);
        state.Open(root);

        var response = await client.GetAsync(
            $"/internal/app-assistant/workbenches/{workbench.WorkbenchId}/context");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<AppAssistantWorkbenchContext>();
        Assert.NotNull(payload);
        Assert.Equal(workbench.WorkbenchId, payload!.WorkbenchId);
    }

    [Fact]
    public async Task InvalidTodoLimitReturnsBadRequestWithStableErrorCode()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var response = await client.GetAsync(
            "/internal/app-assistant/workbenches/unknown/worktrees/unknown/todos?limit=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("INVALID_LIMIT", body!["error"]);
    }
}
