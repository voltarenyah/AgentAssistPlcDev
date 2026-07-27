using Agent.Mcp;
using Agent.Workbench;
using Contracts.Sandbox;

var builder = WebApplication.CreateBuilder(args);
var legacyConfigPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "PlcAiAssistant", "config.json");
// Read-only compatibility for existing server/API-key overrides; never used for workbench storage.
builder.Configuration.AddJsonFile(legacyConfigPath, optional: true, reloadOnChange: false);
var currentConfigPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "AutomationWorkbench", "config.json");
// Current settings load after legacy compatibility so they take precedence.
builder.Configuration.AddJsonFile(currentConfigPath, optional: true, reloadOnChange: false);
builder.Services.AddCors();
builder.Services.AddSingleton<AtomicJsonStore>();
builder.Services.AddSingleton<WorkbenchCatalog>();
builder.Services.AddSingleton<DeviceOperationLock>();
builder.Services.AddSingleton<DeviceReconciler>();
builder.Services.AddSingleton<DeviceSourceResolver>(services =>
{
    var store = services.GetRequiredService<AtomicJsonStore>();
    return new DeviceSourceResolver(device =>
    {
        var path = Path.Combine(device.DeviceRoot, "device.json");
        var metadata = store.Read<DeviceMetadata>(path);
        store.Write(path, metadata with { Knowledge = metadata.Knowledge with { Stale = true } });
    });
});

var startExternalMcp = !builder.Environment.IsEnvironment("Testing")
    && builder.Configuration.GetValue("Mcp:StartExternal", true);
if (startExternalMcp)
{
    builder.Services.AddSingleton<McpRuntime>();
    builder.Services.AddHostedService<McpRuntimeHostedService>();
    builder.Services.AddSingleton<EngineeringCaller>(s => new(s.GetRequiredService<McpRuntime>()));
    builder.Services.AddSingleton<KnowledgeCaller>(s => new(s.GetRequiredService<McpRuntime>()));
    builder.Services.AddSingleton<VersionControlCaller>(s => new(s.GetRequiredService<McpRuntime>()));
    builder.Services.AddSingleton<SourceEditorCaller>(s => new(s.GetRequiredService<McpRuntime>()));
    builder.Services.AddSingleton<ApiMcpGateway>(s => new(
        s.GetRequiredService<EngineeringCaller>(), s.GetRequiredService<KnowledgeCaller>(),
        s.GetRequiredService<VersionControlCaller>(), s.GetRequiredService<SourceEditorCaller>()));
    builder.Services.AddSingleton<WorkbenchCoordinator>(s => new(
        s.GetRequiredService<EngineeringCaller>(),
        s.GetRequiredService<KnowledgeCaller>(),
        s.GetRequiredService<VersionControlCaller>(),
        s.GetRequiredService<WorkbenchCatalog>(),
        s.GetRequiredService<AtomicJsonStore>(),
        s.GetRequiredService<DeviceReconciler>(),
        s.GetRequiredService<DeviceSourceResolver>(),
        s.GetRequiredService<DeviceOperationLock>()));
}
else
{
    builder.Services.AddSingleton<UnavailableCaller>();
    builder.Services.AddSingleton<ApiMcpGateway>(s =>
    {
        var caller = s.GetRequiredService<UnavailableCaller>();
        return new(caller, caller, caller, caller);
    });
    builder.Services.AddSingleton<WorkbenchCoordinator>(s => new(
        s.GetRequiredService<UnavailableCaller>(),
        s.GetRequiredService<UnavailableCaller>(),
        s.GetRequiredService<UnavailableCaller>(),
        s.GetRequiredService<WorkbenchCatalog>(),
        s.GetRequiredService<AtomicJsonStore>(),
        s.GetRequiredService<DeviceReconciler>(),
        s.GetRequiredService<DeviceSourceResolver>(),
        s.GetRequiredService<DeviceOperationLock>()));
}

builder.Services.AddSingleton<WorkbenchApiState>();
builder.Services.AddSingleton<CompatibilityRuntimeState>();
builder.Services.AddSingleton<CompatibilityConfigStore>();
builder.Services.AddSingleton<ApiChatService>();
builder.Services.AddSingleton<SandboxPolicy>();
builder.Services.AddSingleton<DeviceToolArgumentBinder>();
builder.Services.AddSingleton<PendingToolActions>();
builder.Services.AddSingleton<SandboxedToolExecutor>();

var app = builder.Build();
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
app.UseMiddleware<WorkbenchApiExceptionMiddleware>();
app.MapGet("/api/status", () => Results.Ok(new { storage = "workbench", legacyProjects = false }));
app.MapWorkbenchEndpoints();
app.MapCompatibilityEndpoints();
app.Run();

public partial class Program { }

internal sealed class McpRuntime : IAsyncDisposable
{
    public McpRuntime(IConfiguration configuration, CompatibilityRuntimeState state)
    {
        var paths = McpExecutableResolver.Resolve(configuration, AppContext.BaseDirectory);
        Host = new McpHost(
            paths.Engineering, paths.Knowledge, paths.VersionControl, paths.SourceEditor);
        Host.ServerLog += state.Logs.Enqueue;
    }
    public McpHost Host { get; }
    public ValueTask DisposeAsync() => Host.DisposeAsync();
}

internal sealed class McpRuntimeHostedService(McpRuntime runtime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => runtime.Host.StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal abstract class RuntimeCaller(McpRuntime runtime) : IMcpToolCaller
{
    protected abstract IMcpToolCaller Resolve(McpHost host);
    public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default) =>
        Resolve(runtime.Host).CallAsync<T>(tool, args, cancellationToken);
}
internal sealed class EngineeringCaller(McpRuntime runtime) : RuntimeCaller(runtime)
{
    protected override IMcpToolCaller Resolve(McpHost host) => host.Engineering;
}
internal sealed class KnowledgeCaller(McpRuntime runtime) : RuntimeCaller(runtime)
{
    protected override IMcpToolCaller Resolve(McpHost host) => host.Knowledge;
}
internal sealed class VersionControlCaller(McpRuntime runtime) : RuntimeCaller(runtime)
{
    protected override IMcpToolCaller Resolve(McpHost host) => host.VersionControl!;
}
internal sealed class SourceEditorCaller(McpRuntime runtime) : RuntimeCaller(runtime)
{
    protected override IMcpToolCaller Resolve(McpHost host) => host.SourceEditor!;
}
internal sealed class UnavailableCaller : IMcpToolCaller
{
    public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"No MCP test double was registered for '{tool}'.");
}
