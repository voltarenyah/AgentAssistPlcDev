using Agent.Mcp;
using Agent.Chat;
using Agent.Workbench;
using Contracts.Sandbox;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
var isTesting = builder.Environment.IsEnvironment("Testing");
var isDevelopment = builder.Environment.IsDevelopment();
var isProduction = builder.Environment.IsProduction();
if (isTesting)
{
    var testWebRoot = Path.Combine(AppContext.BaseDirectory, "TestWebRoot");
    if (Directory.Exists(testWebRoot))
    {
        builder.Environment.WebRootPath = testWebRoot;
        builder.Environment.WebRootFileProvider = new PhysicalFileProvider(testWebRoot);
    }
}
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
// Re-apply external providers after compatibility files: env/CLI must remain authoritative.
builder.Configuration.AddEnvironmentVariables();
if (args.Length > 0) builder.Configuration.AddCommandLine(args);
var startupOptions = ApplicationStartupOptions.From(
    builder.Configuration,
    builder.Environment.EnvironmentName);
var configuredUrls = builder.Configuration["urls"] ?? builder.Configuration["ASPNETCORE_URLS"];
if (isProduction || string.IsNullOrWhiteSpace(configuredUrls))
    builder.WebHost.UseUrls(startupOptions.Url);
builder.Services.AddCors(options =>
{
    if (isTesting)
    {
        options.AddPolicy("WorkbenchCors", policy => policy
            .WithOrigins(builder.Configuration["Cors:TestingOrigin"] ?? "http://testing.local")
            .AllowAnyHeader()
            .AllowAnyMethod());
    }
    else if (isDevelopment)
    {
        options.AddPolicy("WorkbenchCors", policy => policy
            .WithOrigins(builder.Configuration["Cors:ViteOrigin"] ?? "http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod());
    }
});
builder.Services.AddSingleton<AtomicJsonStore>();
builder.Services.AddSingleton<WorktreeTaskStore>();
builder.Services.AddSingleton<WorkbenchCatalog>();
builder.Services.AddSingleton(_ => new TrustedWorkbenchRootRegistry(
    builder.Configuration["Sandbox:TrustedRootsFile"]
    ?? (builder.Environment.IsEnvironment("Testing")
        ? Path.Combine(
            Path.GetTempPath(),
            "AutomationWorkbench.Tests",
            Guid.NewGuid().ToString("N"),
            "trusted-workbench-roots.json")
        : null)));
builder.Services.AddSingleton(services => SandboxConfig.Load(
    builder.Configuration["Sandbox:ConfigFile"] ?? SandboxConfig.DefaultFilePath,
    services.GetRequiredService<TrustedWorkbenchRootRegistry>().FilePath));
builder.Services.AddSingleton<DeviceOperationLock>();
builder.Services.AddSingleton<DeviceReconciler>();
builder.Services.AddSingleton<DeviceSnapshotReader>();
builder.Services.AddSingleton<WorkbenchWritePolicy>();
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
builder.Services.AddSingleton<OperationStatusRegistry>();

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
        s.GetRequiredService<DeviceOperationLock>(),
        s.GetRequiredService<SandboxConfig>().PathJail));
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
        s.GetRequiredService<DeviceOperationLock>(),
        s.GetRequiredService<SandboxConfig>().PathJail));
}

builder.Services.AddSingleton<WorkbenchApiState>();
builder.Services.AddSingleton<CompatibilityRuntimeState>();
builder.Services.AddSingleton(_ => new CompatibilityConfigStore());
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
builder.Services.AddSingleton<DeepSeekBalanceClient>();
builder.Services.AddSingleton<ApiChatService>();
builder.Services.AddSingleton<SandboxPolicy>();
builder.Services.AddSingleton<DeviceToolArgumentBinder>();
builder.Services.AddSingleton<PendingToolActions>();
builder.Services.AddSingleton<SandboxedToolExecutor>();

var app = builder.Build();
if (isTesting || isDevelopment)
    app.UseCors("WorkbenchCors");
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    await next();

    if (context.Response.StatusCode != StatusCodes.Status404NotFound
        || context.Response.HasStarted
        || context.Request.Method is not ("GET" or "HEAD")
        || context.Request.Path.StartsWithSegments("/api"))
        return;

    var indexPath = Path.Combine(app.Environment.WebRootPath ?? string.Empty, "index.html");
    if (!File.Exists(indexPath))
        return;

    context.Response.Clear();
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(indexPath);
});
app.UseMiddleware<WorkbenchApiExceptionMiddleware>();
var applicationVersion = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? typeof(Program).Assembly.GetName().Version?.ToString()
    ?? "unknown";
app.MapPost("/api/lifecycle/shutdown", (
    HttpRequest request,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime) =>
{
    var expected = configuration["Application:ShutdownToken"];
    var supplied = request.Headers["X-AutomationWorkbench-Shutdown-Token"].ToString();
    if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied))
        return Results.Unauthorized();

    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    if (expectedBytes.Length != suppliedBytes.Length
        || !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
        return Results.Unauthorized();

    lifetime.StopApplication();
    return Results.Accepted();
});
app.MapGet("/api/status", () => Results.Ok(new
{
    storage = "workbench",
    legacyProjects = false,
    version = applicationVersion,
}));
app.MapWorkbenchEndpoints();
app.MapCompatibilityEndpoints();
var browserUrl = isProduction || string.IsNullOrWhiteSpace(configuredUrls)
    ? startupOptions.Url
    : configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).First();
try
{
    await app.StartAsync();
    if (startupOptions.OpenBrowserOnStart)
        BrowserLauncher.Open(browserUrl);
    await app.WaitForShutdownAsync();
}
catch (Exception exception) when (ApplicationStartupOptions.IsAddressInUse(exception))
{
    Console.Error.WriteLine(ApplicationStartupOptions.PortInUseMessage(startupOptions));
    Environment.ExitCode = 1;
}

public partial class Program { }

internal sealed class McpRuntime : IAsyncDisposable
{
    public McpRuntime(
        IConfiguration configuration,
        CompatibilityRuntimeState state,
        TrustedWorkbenchRootRegistry trustedRoots)
    {
        var paths = McpExecutableResolver.Resolve(configuration, AppContext.BaseDirectory);
        McpExecutableResolver.Validate(paths);
        var sandboxEnvironment = new Dictionary<string, string?>
        {
            [TrustedWorkbenchRootRegistry.EnvironmentVariableName] = trustedRoots.FilePath,
        };
        Host = new McpHost(
            paths.Engineering, paths.Knowledge, paths.VersionControl, paths.SourceEditor,
            sandboxEnvironment);
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

internal abstract class RuntimeCaller(McpRuntime runtime) : IProgressMcpToolCaller
{
    protected abstract IMcpToolCaller Resolve(McpHost host);
    public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default) =>
        Resolve(runtime.Host).CallAsync<T>(tool, args, cancellationToken);

    public Task<T> CallAsync<T>(
        string tool,
        object args,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken = default)
    {
        var caller = Resolve(runtime.Host);
        return caller is IProgressMcpToolCaller progressCaller
            ? progressCaller.CallAsync<T>(tool, args, progress, cancellationToken)
            : caller.CallAsync<T>(tool, args, cancellationToken);
    }
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

internal static class BrowserLauncher
{
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // Browser launch is a convenience. It must not take down a healthy
            // API process when ShellExecute is unavailable or denied.
            Console.Error.WriteLine($"Could not open browser at {url}: {exception.Message}");
        }
    }
}
