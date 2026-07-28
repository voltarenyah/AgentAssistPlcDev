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
    public async Task SessionsListsTiaProcessesBeforeAWorkbenchIsSelected()
    {
        var engineering = new RecordingToolCaller("[{\"sessionId\":17,\"projectName\":\"Demo\"}]");
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ApiMcpGateway>();
                services.AddSingleton(new ApiMcpGateway(
                    engineering,
                    new RecordingToolCaller(),
                    new RecordingToolCaller(),
                    new RecordingToolCaller()));
            });
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"sessionId\":17", await response.Content.ReadAsStringAsync());
        Assert.Equal(["list_sessions"], engineering.Calls);
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

    [Fact]
    public void OperationRegistryKeepsOnlyLatestStatusAndDismissesTerminalSnapshots()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-28T00:00:00Z"));
        var registry = new OperationStatusRegistry(clock);

        registry.Start("op-1", "create-workbench", "Preparing workbench storage...");
        registry.Report("op-1", "Initializing Git repository...");

        Assert.True(registry.TryGet("op-1", out var running));
        Assert.Equal("op-1", running.OperationId);
        Assert.Equal("create-workbench", running.OperationType);
        Assert.Equal("running", running.State);
        Assert.Equal("Initializing Git repository...", running.Message);
        Assert.Null(running.ErrorMessage);

        registry.Succeed("op-1", "Workbench created.");
        registry.Dismiss("op-1");

        Assert.False(registry.TryGet("op-1", out _));
        Assert.False(registry.TryGet("missing", out _));
    }

    [Fact]
    public void OperationRegistryRetainsFailureUntilDismissedOrExpired()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-28T00:00:00Z"));
        var registry = new OperationStatusRegistry(clock);

        registry.Start("op-1", "refresh", "Exporting block Main_OB1...");
        registry.Fail("op-1", "Exporting block Main_OB1...", "TIA export failed.");

        Assert.True(registry.TryGet("op-1", out var failed));
        Assert.Equal("failed", failed.State);
        Assert.Equal("Exporting block Main_OB1...", failed.Message);
        Assert.Equal("TIA export failed.", failed.ErrorMessage);

        clock.Advance(TimeSpan.FromMinutes(61));

        Assert.False(registry.TryGet("op-1", out _));
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
            "requester",
            CancellationToken.None);
        var id = requested!.GetType().GetProperty("_confirmationId")!.GetValue(requested)!.ToString()!;

        await pending.ResolveAsync(id, ToolConfirmation.AllowOnce, DeviceContextIdentity.Key(Context()), "requester");
        Assert.Single(caller.Calls);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => pending.ResolveAsync(id, ToolConfirmation.AllowOnce, DeviceContextIdentity.Key(Context()), "requester"));

        var rejected = await executor.RequestAsync("vc_restore", new Dictionary<string, object?>(), Context(), "requester", CancellationToken.None);
        var rejectedId = rejected!.GetType().GetProperty("_confirmationId")!.GetValue(rejected)!.ToString()!;
        await pending.ResolveAsync(rejectedId, ToolConfirmation.Deny, DeviceContextIdentity.Key(Context()), "requester");
        Assert.Single(caller.Calls);
    }

    [Fact]
    public void ChatIdentitySeparatesSameDeviceIdAcrossWorktrees()
    {
        var first = Context();
        var second = first with { WorktreeId = "other-worktree" };
        Assert.NotEqual(DeviceContextIdentity.Key(first), DeviceContextIdentity.Key(second));
    }

    [Theory]
    [InlineData("src_validate", "source")]
    [InlineData("get_schema", "knowledge")]
    [InlineData("query", "knowledge")]
    [InlineData("get_block", "knowledge")]
    [InlineData("search", "knowledge")]
    [InlineData("vc_status", "vc")]
    [InlineData("list_blocks", "engineering")]
    public void GatewayRoutesEveryToolFamilyToItsOwner(string tool, string owner)
    {
        var engineering = new RecordingToolCaller();
        var knowledge = new RecordingToolCaller();
        var vc = new RecordingToolCaller();
        var source = new RecordingToolCaller();
        var gateway = new ApiMcpGateway(engineering, knowledge, vc, source);

        Assert.Same(owner switch
        {
            "knowledge" => knowledge,
            "vc" => vc,
            "source" => source,
            _ => engineering,
        }, gateway.For(tool));
    }

    [Fact]
    public void PartialExportIsRejectedWithoutTouchingStagedSnapshot()
    {
        var context = Context();
        Directory.CreateDirectory(context.StagingRoot);
        var snapshot = Path.Combine(context.StagingRoot, "metadata.json");
        File.WriteAllText(snapshot, "keep");
        var binder = new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { }));

        Assert.Throws<WorkbenchLifecycleException>(() =>
            binder.Bind("export_block", new Dictionary<string, object?>(), context));
        Assert.Equal("keep", File.ReadAllText(snapshot));
    }

    [Fact]
    public async Task PendingConfirmationRejectsWrongContextAndExpires()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var pending = new PendingToolActions(clock, TimeSpan.FromSeconds(1));
        var id = pending.Add("right", "requester", (_, _) => Task.FromResult<object?>(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pending.ResolveAsync(id, ToolConfirmation.AllowOnce, "wrong", "requester"));
        Assert.True((bool)(await pending.ResolveAsync(id, ToolConfirmation.AllowOnce, "right", "requester"))!);

        var expired = pending.Add("right", "requester", (_, _) => Task.FromResult<object?>(true));
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            pending.ResolveAsync(expired, ToolConfirmation.AllowOnce, "right", "requester"));
    }

    [Fact]
    public void KnowledgeAndSourceReadsAreBoundToSelectedDevice()
    {
        var context = Context();
        Directory.CreateDirectory(context.ExportedSourceRoot);
        var source = Path.Combine(context.ExportedSourceRoot, "A.xml");
        File.WriteAllText(source, "<a/>");
        var binder = new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { }));

        var knowledge = binder.Bind("get_schema", new Dictionary<string, object?>(), context);
        Assert.Equal(context.KnowledgeDbPath, knowledge["dbPath"]);
        Assert.Throws<ArgumentException>(() => binder.Bind(
            "search", new Dictionary<string, object?> { ["dbPath"] = Path.Combine(root, "other.db") }, context));
        var parsed = binder.Bind("src_parse_block", new Dictionary<string, object?> { ["xmlFilePath"] = source }, context);
        Assert.Equal(source, parsed["xmlFilePath"]);
        Assert.Throws<ArgumentException>(() => binder.Bind(
            "src_validate", new Dictionary<string, object?> { ["xmlFilePath"] = Path.Combine(root, "foreign.xml") }, context));
    }

    [Fact]
    public async Task ExpiryActivelyDeniesWaitingConfirmation()
    {
        var pending = new PendingToolActions(TimeProvider.System, TimeSpan.FromMilliseconds(30));
        var released = new TaskCompletionSource<ToolConfirmation>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Add("context", "requester", (decision, _) =>
        {
            released.TrySetResult(decision);
            return Task.FromResult<object?>(null);
        });

        Assert.Equal(ToolConfirmation.Deny, await released.Task.WaitAsync(TimeSpan.FromSeconds(2)));
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
        var runtimeState = new CompatibilityRuntimeState();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.ConfigureServices(services =>
            {
                services.RemoveAll<WorkbenchCatalog>();
                services.RemoveAll<AtomicJsonStore>();
                services.RemoveAll<WorkbenchApiState>();
                services.RemoveAll<CompatibilityRuntimeState>();
                services.RemoveAll<CompatibilityConfigStore>();
                services.AddSingleton(store);
                services.AddSingleton(catalog);
                services.AddSingleton<WorkbenchApiState>();
                services.AddSingleton(runtimeState);
                services.AddSingleton(new CompatibilityConfigStore(Path.Combine(root, "config.json")));
            });
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/workbenches/{wb.WorkbenchId}/worktrees/{wtId}/devices/dev-1/select", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/devices/dev-1/sessions")).StatusCode);
        var createdSessionResponse = await client.PostAsync("/api/chat/session/new", null);
        createdSessionResponse.EnsureSuccessStatusCode();
        var createdSession = await createdSessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = createdSession.GetProperty("header").GetProperty("sessionId").GetString()!;
        var secondSessionResponse = await client.PostAsync("/api/chat/session/new", null);
        var secondSession = await secondSessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondId = secondSession.GetProperty("header").GetProperty("sessionId").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/chat/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/chat/clear", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(
            "/api/chat/session/delete", new { sessionId = secondId })).StatusCode);
        var deletedInfo = await client.GetFromJsonAsync<JsonElement>("/api/chat/session/info");
        Assert.True(deletedInfo.GetProperty("requiresExplicitSession").GetBoolean());
        var thirdSession = await (await client.PostAsync("/api/chat/session/new", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        var thirdId = thirdSession.GetProperty("header").GetProperty("sessionId").GetString()!;
        var newInfo = await client.GetFromJsonAsync<JsonElement>("/api/chat/session/info");
        Assert.False(newInfo.GetProperty("requiresExplicitSession").GetBoolean());
        await client.PostAsJsonAsync("/api/chat/session/delete", new { sessionId = thirdId });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            "/api/chat/session/load", new { sessionId })).StatusCode);
        var loadedInfo = await client.GetFromJsonAsync<JsonElement>("/api/chat/session/info");
        Assert.False(loadedInfo.GetProperty("requiresExplicitSession").GetBoolean());
        var generation = runtimeState.ChatGeneration;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(
            "/api/config/settings",
            new { model = "new-model", thinkingEnabled = false, reasoningEffort = "low", temperature = 0.2, topP = 0.8 })).StatusCode);
        Assert.Equal(generation + 1, runtimeState.ChatGeneration);
        Assert.Equal("new-model", runtimeState.ChatSettings!.Value.GetProperty("model").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(
            "/api/config/key", new { apiKey = "replacement" })).StatusCode);
        Assert.Equal(generation + 2, runtimeState.ChatGeneration);
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

    [Fact]
    public void OpeningPersistedWorkbenchRegistersItsCustomRootForMcpSandboxes()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "defaults"));
        var customRoot = Path.Combine(root, "chosen", "Line");
        var created = catalog.Create("Line", customRoot);
        var registry = new TrustedWorkbenchRootRegistry(Path.Combine(root, "trusted-roots.json"));
        var state = new WorkbenchApiState(catalog, store, registry);

        state.Open(created.RootPath);

        Assert.Contains(
            registry.Read(),
            registered => string.Equals(registered, created.RootPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OpeningWorkbenchRejectsMetadataThatRedirectsTrustToAnotherRoot()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "defaults"));
        var customRoot = Path.Combine(root, "chosen", "Line");
        var created = catalog.Create("Line", customRoot);
        var redirectedRoot = Path.Combine(root, "unregistered");
        store.Write(
            Path.Combine(customRoot, "workbench.json"),
            created with { RootPath = redirectedRoot });
        Directory.CreateDirectory(redirectedRoot);
        store.Write(
            Path.Combine(redirectedRoot, "workbench.json"),
            created with { RootPath = redirectedRoot });
        var registry = new TrustedWorkbenchRootRegistry(Path.Combine(root, "trusted-roots.json"));
        var state = new WorkbenchApiState(catalog, store, registry);

        Assert.Throws<WorkbenchCatalogException>(() => state.Open(customRoot));
        Assert.DoesNotContain(
            registry.Read(),
            registered => string.Equals(registered, redirectedRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CatalogReloadRemovesDeletedWorkbenchFromTrustedRegistry()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "defaults"));
        var created = catalog.Create("Line", Path.Combine(root, "custom", "Line"));
        var registry = new TrustedWorkbenchRootRegistry(Path.Combine(root, "trusted-roots.json"));
        var state = new WorkbenchApiState(catalog, store, registry);
        state.Open(created.RootPath);
        Assert.Contains(created.RootPath, registry.Read(), StringComparer.OrdinalIgnoreCase);

        File.Delete(Path.Combine(created.RootPath, "workbench.json"));
        Assert.Empty(state.List());
        Assert.Empty(registry.Read());
    }

    [Fact]
    public void McpHostPassesOnlyTrustedRegistryLocationToSandboxedServers()
    {
        var registryPath = Path.Combine(root, "trusted-roots.json");
        var environment = new Dictionary<string, string?>
        {
            [TrustedWorkbenchRootRegistry.EnvironmentVariableName] = registryPath,
        };

        var host = new McpHost("engineering.exe", "knowledge.exe", "vc.exe", "source.exe", environment);

        Assert.Equal(registryPath,
            host.Engineering.EnvironmentVariables[TrustedWorkbenchRootRegistry.EnvironmentVariableName]);
        Assert.Equal(registryPath,
            host.SourceEditor!.EnvironmentVariables[TrustedWorkbenchRootRegistry.EnvironmentVariableName]);
        Assert.Empty(host.Knowledge.EnvironmentVariables);
        Assert.Empty(host.VersionControl!.EnvironmentVariables);
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

    private sealed class RecordingToolCaller(string json = "{}") : IMcpToolCaller
    {
        public List<string> Calls { get; } = [];
        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            return Task.FromResult((T)(object)JsonDocument.Parse(json).RootElement.Clone());
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
