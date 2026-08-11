using Agent.Workbench;
using ApiHost.AppAssistant;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class AppAssistantGatewayTests
{
    [Fact]
    public async Task ContextContainsRuntimeActionsWithoutFilesystemPaths()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var gateway = factory.Services.GetRequiredService<AppAssistantGateway>();
        var root = Path.Combine(Path.GetTempPath(), "assistant-context-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Assistant Context", root);
        state.Open(root);

        var context = await gateway.GetContextAsync(workbench.WorkbenchId);

        Assert.Equal(workbench.WorkbenchId, context.WorkbenchId);
        Assert.Contains(context.AvailableActions, action => action.Id == "read_svn_state");
        var create = Assert.Single(context.AvailableActions, action => action.Id == "create_worktree");
        Assert.DoesNotContain(root, System.Text.Json.JsonSerializer.Serialize(context));
        Assert.NotNull(create);
    }

    [Fact]
    public async Task ContextOmitsHistoryUntilTheAssistantRequestsIt()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var gateway = factory.Services.GetRequiredService<AppAssistantGateway>();
        var root = Path.Combine(Path.GetTempPath(), "assistant-history-focus-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Assistant History Focus", root);
        state.Open(root);

        var context = await gateway.GetContextAsync(workbench.WorkbenchId);

        Assert.Null(context.History);
    }

    [Fact]
    public async Task AssistantActionsKeepWorktreeCreationDisabledUntilMutationPlan()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var gateway = factory.Services.GetRequiredService<AppAssistantGateway>();
        var root = Path.Combine(Path.GetTempPath(), "assistant-actions-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Assistant Actions", root);
        state.Open(root);

        var actions = await gateway.GetActionsAsync(workbench.WorkbenchId);
        var create = Assert.Single(actions, action => action.Id == "create_worktree");

        Assert.False(create.Enabled);
        Assert.Contains(create.BlockedBy, blocker => blocker.Contains("approved mutation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ContextRefreshesChangedWorktreeFactsWithoutDuplicatingTheWorktree()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var store = factory.Services.GetRequiredService<AtomicJsonStore>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var gateway = factory.Services.GetRequiredService<AppAssistantGateway>();
        var root = Path.Combine(Path.GetTempPath(), "assistant-runtime-facts-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Assistant Runtime Facts", root);
        workbench = catalog.RegisterWorktree(
            workbench,
            new WorkbenchWorktreeRegistration("wt-1", "master", "master", "master"));
        var worktreeRoot = WorkbenchPaths.ResolveWorktree(root, "master");
        Directory.CreateDirectory(worktreeRoot);
        store.Write(
            Path.Combine(worktreeRoot, "worktree.json"),
            new WorktreeMetadata(
                WorkbenchSchema.CurrentVersion,
                "wt-1",
                workbench.WorkbenchId,
                "master",
                "master",
                DateTimeOffset.UtcNow.ToString("O"),
                null,
                null,
                null,
                Array.Empty<string>(),
                null));
        state.Open(root);

        _ = await gateway.GetContextAsync(workbench.WorkbenchId);
        store.Write(
            Path.Combine(worktreeRoot, "worktree.json"),
            new WorktreeMetadata(
                WorkbenchSchema.CurrentVersion,
                "wt-1",
                workbench.WorkbenchId,
                "Feature A",
                "feature/a",
                DateTimeOffset.UtcNow.ToString("O"),
                null,
                null,
                null,
                Array.Empty<string>(),
                null));
        EngineeringStateWriter.Write(
            worktreeRoot,
            EngineeringStateWriter.Create("^/native/main", 4, null, null, EngineeringCompileStatus.Success));
        var tasks = new WorktreeTaskStore(store);
        tasks.Add(worktreeRoot, "Fix that bug");
        var secondTask = tasks.Add(worktreeRoot, "Implement new function");
        tasks.Update(worktreeRoot, secondTask.TaskId, task => task with { Status = WorktreeTaskStatus.InProgress });

        var context = await gateway.GetContextAsync(workbench.WorkbenchId);

        var summary = Assert.Single(context.Runtime.Worktrees);
        Assert.Equal("Feature A", summary.Name);
        Assert.Equal(4, summary.SvnCurrentRevision);
        Assert.Equal(2, summary.TodoCount);
    }

    [Fact]
    public async Task TodoReadRejectsAWorktreeOutsideTheRequestedWorkbench()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var gateway = factory.Services.GetRequiredService<AppAssistantGateway>();
        var firstRoot = Path.Combine(Path.GetTempPath(), "assistant-first-" + Guid.NewGuid().ToString("N"));
        var secondRoot = Path.Combine(Path.GetTempPath(), "assistant-second-" + Guid.NewGuid().ToString("N"));
        var first = catalog.Create("First", firstRoot);
        var second = catalog.Create("Second", secondRoot);
        state.Open(firstRoot);
        state.Open(secondRoot);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            gateway.GetTodosAsync(first.WorkbenchId, second.WorkbenchId));
    }
}
