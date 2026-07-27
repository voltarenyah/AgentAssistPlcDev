using Agent.Mcp;
using Agent.Workbench;

var builder = WebApplication.CreateBuilder(args);
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

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddSingleton<McpRuntime>();
    builder.Services.AddHostedService<McpRuntimeHostedService>();
    builder.Services.AddSingleton<EngineeringCaller>(s => new(s.GetRequiredService<McpRuntime>()));
    builder.Services.AddSingleton<KnowledgeCaller>(s => new(s.GetRequiredService<McpRuntime>()));
    builder.Services.AddSingleton<VersionControlCaller>(s => new(s.GetRequiredService<McpRuntime>()));
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

var app = builder.Build();
app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
app.UseMiddleware<WorkbenchApiExceptionMiddleware>();
app.MapGet("/api/status", () => Results.Ok(new { storage = "workbench", legacyProjects = false }));
app.MapWorkbenchEndpoints();
app.Run();

public partial class Program { }

internal sealed class McpRuntime : IAsyncDisposable
{
    public McpRuntime(IConfiguration configuration)
    {
        Host = new McpHost(
            Required(configuration, "Mcp:Engineering"),
            Required(configuration, "Mcp:Knowledge"),
            Required(configuration, "Mcp:VersionControl"),
            Required(configuration, "Mcp:SourceEditor"));
    }
    public McpHost Host { get; }
    public ValueTask DisposeAsync() => Host.DisposeAsync();
    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] ?? throw new InvalidOperationException($"Missing configuration '{key}'.");
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
internal sealed class UnavailableCaller : IMcpToolCaller
{
    public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"No MCP test double was registered for '{tool}'.");
}
