using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent.Workbench;
using ApiHost.AppAssistant;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class AppAssistantWorktreeMutationTests
{
    [Fact]
    public async Task StaleRevisionIsRejectedBeforeWorktreeCreation()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var root = Path.Combine(Path.GetTempPath(), "assistant-mutation-stale-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Mutation Stale", root);
        state.Open(root);
        state.Select(workbench.WorkbenchId, "user-selected", null);

        var response = await client.PostAsJsonAsync(
            $"/internal/app-assistant/workbenches/{workbench.WorkbenchId}/mutations/create-worktree",
            new CreateWorktreeAssistantRequest(
                workbench.WorkbenchId, "feature-a", "feature/a", "master", 0, "request-stale"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>();
        Assert.Equal("CONTEXT_STALE", body!["error"].GetString());
        Assert.Equal("user-selected", state.Selection!.WorktreeId);
    }

    [Fact]
    public async Task InvalidMutationNameIsRejectedWithoutAPathArgument()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var root = Path.Combine(Path.GetTempPath(), "assistant-mutation-name-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Mutation Name", root);
        state.Open(root);

        var response = await client.PostAsJsonAsync(
            $"/internal/app-assistant/workbenches/{workbench.WorkbenchId}/mutations/create-worktree",
            new CreateWorktreeAssistantRequest(
                workbench.WorkbenchId, "..\\escape", "feature/a", "master", 0, "request-name"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("INVALID_WORKTREE_NAME", body!["error"]);
    }

    [Fact]
    public async Task RepeatedRequestIdReturnsTheCachedFailureWithoutAnotherTransition()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var gateway = factory.Services.GetRequiredService<AppAssistantGateway>();
        var root = Path.Combine(Path.GetTempPath(), "assistant-mutation-repeat-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Mutation Repeat", root);
        state.Open(root);
        var request = new CreateWorktreeAssistantRequest(
            workbench.WorkbenchId, "feature-a", "feature/a", "master", 0, "request-repeat");

        await Assert.ThrowsAsync<WorkbenchCatalogException>(() =>
            gateway.CreateWorktreeAsync(workbench.WorkbenchId, request));
        var afterFirst = factory.Services
            .GetRequiredService<WorkbenchRuntimeStateCoordinator>()
            .GetSnapshot(workbench.WorkbenchId);
        await Assert.ThrowsAsync<WorkbenchCatalogException>(() =>
            gateway.CreateWorktreeAsync(workbench.WorkbenchId, request));
        var afterSecond = factory.Services
            .GetRequiredService<WorkbenchRuntimeStateCoordinator>()
            .GetSnapshot(workbench.WorkbenchId);

        Assert.Equal(afterFirst.WorkbenchRevision, afterSecond.WorkbenchRevision);
    }
}
