using System.Net;
using System.Net.Http.Json;
using Agent.Workbench;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class RuntimeStateEndpointsTests
{
    [Fact]
    public async Task RuntimeStateEndpointReturnsCoordinatorSnapshotForRegisteredWorkbench()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var coordinator = factory.Services.GetRequiredService<WorkbenchRuntimeStateCoordinator>();
        var root = Path.Combine(Path.GetTempPath(), "runtime-state-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Runtime State", root);
        state.Open(root);
        coordinator.Refresh(workbench.WorkbenchId, Array.Empty<WorktreeRuntimeSummary>());

        var response = await client.GetAsync($"/api/workbenches/{workbench.WorkbenchId}/runtime-state");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<WorkbenchRuntimeSnapshot>();
        Assert.NotNull(payload);
        Assert.Equal(workbench.WorkbenchId, payload!.WorkbenchId);
        Assert.Equal(1, payload.WorkbenchRevision);
    }

    [Fact]
    public async Task RuntimeStateEndpointRefreshesPersistedWorktreeFactsBeforeReturningSnapshot()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var store = factory.Services.GetRequiredService<AtomicJsonStore>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var root = Path.Combine(Path.GetTempPath(), "runtime-state-refresh-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Runtime State Refresh", root);
        workbench = catalog.RegisterWorktree(
            workbench,
            new WorkbenchWorktreeRegistration("wt-1", "master", "master", "master"));
        var worktreeRoot = WorkbenchPaths.ResolveWorktree(root, "master");
        store.Write(
            Path.Combine(worktreeRoot, "worktree.json"),
            new WorktreeMetadata(
                WorkbenchSchema.CurrentVersion,
                "wt-1",
                workbench.WorkbenchId,
                "master",
                "master",
                DateTimeOffset.UtcNow.ToString("O"),
                "abc123",
                null,
                null,
                Array.Empty<string>(),
                null));
        state.Open(root);

        var response = await client.GetAsync($"/api/workbenches/{workbench.WorkbenchId}/runtime-state");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<WorkbenchRuntimeSnapshot>();
        var summary = Assert.Single(payload!.Worktrees);
        Assert.Equal("master", summary.Branch);
        Assert.Equal("abc123", summary.Head);
    }

    [Fact]
    public async Task WorkbenchSelectionUpdatesRuntimeFocus()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var coordinator = factory.Services.GetRequiredService<WorkbenchRuntimeStateCoordinator>();
        var root = Path.Combine(Path.GetTempPath(), "runtime-selection-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Runtime Selection", root);
        state.Open(root);

        var response = await client.PostAsync(
            $"/api/workbenches/{workbench.WorkbenchId}/select",
            new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var snapshot = coordinator.GetSnapshot(workbench.WorkbenchId);
        Assert.Null(snapshot.Focus.WorktreeId);
        Assert.Null(snapshot.Focus.DeviceId);
        Assert.Equal(1, snapshot.WorkbenchRevision);
    }

    [Fact]
    public async Task UnknownWorkbenchRuntimeStateReturnsNotFound()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/workbenches/unknown/runtime-state");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
