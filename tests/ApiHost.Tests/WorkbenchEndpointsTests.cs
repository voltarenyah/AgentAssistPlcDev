using Agent.Workbench;
using Agent.Mcp;
using Agent.Chat;
using Contracts.Sandbox;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Text.Json;
using System.Net;
using Xunit;

public sealed class WorkbenchEndpointsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "api-workbench-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TestingHostStartsWithoutExternalProcessesAndMapsUnknownIdToNotFound()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var status = await client.GetAsync("/api/status");
        var missing = await client.GetAsync("/api/workbenches/missing");

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public void ProductionResolverUsesRepositoryDefaultsWithoutMcpConfiguration()
    {
        var configuration = new ConfigurationBuilder().Build();
        var paths = McpExecutableResolver.Resolve(configuration, AppContext.BaseDirectory);

        Assert.EndsWith(Path.Combine("Mcp.Engineering", "bin", "Debug", "net48", "Mcp.Engineering.exe"), paths.Engineering);
        Assert.EndsWith(Path.Combine("Mcp.Knowledge", "bin", "Debug", "net8.0", "Mcp.Knowledge.exe"), paths.Knowledge);
    }

    [Fact]
    public async Task ProductionHostCanReachListeningPipelineWithExternalStartupDisabled()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Mcp:StartExternal", "false");
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
    }

    [Theory]
    [InlineData("/api/project/info")]
    [InlineData("/api/blocks")]
    [InlineData("/api/knowledge/node-kinds")]
    [InlineData("/api/vc/status")]
    [InlineData("/api/chat")]
    public async Task RestoredDeviceScopedEndpointsRejectNoSelection(string endpoint)
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var response = endpoint == "/api/chat"
            ? await client.PostAsJsonAsync(endpoint, new { message = "hello" })
            : await client.GetAsync(endpoint);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void BinderRejectsConflictingStoragePath()
    {
        var device = Context();
        var binder = new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { }));

        Assert.Throws<ArgumentException>(() => binder.Bind(
            "vc_status",
            new Dictionary<string, object?> { ["repoPath"] = Path.Combine(root, "other") },
            device));
    }

    [Fact]
    public async Task DestructiveConfirmationExecutesOnceAndRejectionDoesNotExecute()
    {
        var caller = new RecordingToolCaller();
        var gateway = new ApiMcpGateway(caller, caller, caller, caller);
        var pending = new PendingToolActions();
        var executor = new SandboxedToolExecutor(
            new SandboxPolicy(),
            new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { })),
            gateway,
            pending);
        var requested = await executor.RequestAsync(
            "vc_restore",
            new Dictionary<string, object?> { ["filePath"] = "Blocks/A.xml" },
            Context(),
            CancellationToken.None);
        var id = requested!.GetType().GetProperty("_confirmationId")!.GetValue(requested)!.ToString()!;

        await pending.ResolveAsync(id, ToolConfirmation.AllowOnce);
        Assert.Single(caller.Calls);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => pending.ResolveAsync(id, ToolConfirmation.AllowOnce));

        var rejected = await executor.RequestAsync("vc_restore", new Dictionary<string, object?>(), Context(), CancellationToken.None);
        var rejectedId = rejected!.GetType().GetProperty("_confirmationId")!.GetValue(rejected)!.ToString()!;
        await pending.ResolveAsync(rejectedId, ToolConfirmation.Deny);
        Assert.Single(caller.Calls);
    }

    [Fact]
    public void ChatIdentitySeparatesSameDeviceIdAcrossWorktrees()
    {
        var first = Context();
        var second = first with { WorktreeId = "other-worktree" };
        Assert.NotEqual(DeviceContextIdentity.Key(first), DeviceContextIdentity.Key(second));
    }

    [Fact]
    public async Task SelectionResolvesRegisteredDeviceAndUnknownApprovalIsConflict()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, root);
        var wb = catalog.Create("Line", null);
        var wtId = "wt-1";
        wb = catalog.RegisterWorktree(wb, new(wtId, "master", "master", "master"));
        var wtRoot = Path.Combine(wb.RootPath, "worktrees", "master");
        Directory.CreateDirectory(wtRoot);
        var wt = new WorktreeMetadata("1.0", wtId, wb.WorkbenchId, "master", "master",
            DateTimeOffset.UtcNow.ToString("O"), null, null, null, ["dev-1"], null);
        store.Write(Path.Combine(wtRoot, "worktree.json"), wt);
        var deviceRoot = Path.Combine(wtRoot, "devices", "PLC_1");
        Directory.CreateDirectory(deviceRoot);
        store.Write(Path.Combine(deviceRoot, "device.json"),
            new DeviceMetadata("1.0", "dev-1", wtId, "PLC:1", "PLC:1", null, null, null,
                new KnowledgeState(true, new Dictionary<string, string>(), null), []));

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.ConfigureServices(services =>
            {
                services.RemoveAll<WorkbenchCatalog>();
                services.RemoveAll<AtomicJsonStore>();
                services.RemoveAll<WorkbenchApiState>();
                services.AddSingleton(store);
                services.AddSingleton(catalog);
                services.AddSingleton<WorkbenchApiState>();
            });
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/workbenches/{wb.WorkbenchId}/worktrees/{wtId}/devices/dev-1/select", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/devices/dev-1/sessions")).StatusCode);
        var conflict = await client.PostAsJsonAsync("/api/devices/dev-1/refresh/apply",
            new RefreshApplyApiRequest("unknown", []));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public void OpenAndSelectUseImmutableIdsAndIgnoreLegacyExports()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, root);
        var created = catalog.Create("Line 1", null);
        Directory.CreateDirectory(Path.Combine(root, "PlcAiAssistant", "exports", "legacy"));

        var state = new WorkbenchApiState(catalog, store);

        Assert.Single(state.List());
        Assert.Equal(created.WorkbenchId, state.List()[0].WorkbenchId);
        state.Select(created.WorkbenchId);
        Assert.Equal(created.WorkbenchId, state.Selection!.WorkbenchId);
    }

    [Fact]
    public void UnknownApprovalCannotBeReusedOrAppliedToAnotherDevice()
    {
        var store = new AtomicJsonStore();
        var state = new WorkbenchApiState(new WorkbenchCatalog(store, root), store);
        var preview = new ReconciliationPreview("approval", "wt", "device-a", "base", "stage", []);
        state.Remember(preview);

        Assert.Throws<KeyNotFoundException>(() => state.Take("approval", "device-b"));
        Assert.Throws<KeyNotFoundException>(() => state.Take("missing", "device-a"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private DeviceContext Context() => new(
        "wb", "wt", "device", root, Path.Combine(root, "worktree"),
        Path.Combine(root, "worktree", "devices", "PLC"),
        Path.Combine(root, "worktree", "devices", "PLC", "exported-source"),
        Path.Combine(root, "worktree", "devices", "PLC", "modified-source"),
        Path.Combine(root, "worktree", "devices", "PLC", "staging"),
        Path.Combine(root, "worktree", "devices", "PLC", "plc-knowledge.db"));

    private sealed class RecordingToolCaller : IMcpToolCaller
    {
        public List<string> Calls { get; } = [];
        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            return Task.FromResult((T)(object)JsonDocument.Parse("{}").RootElement.Clone());
        }
    }
}
