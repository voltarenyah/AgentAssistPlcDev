using Contracts;
using Contracts.Sandbox;
using Mcp.Engineering.Adapter;
using Mcp.Engineering.Openness;
using Mcp.Engineering.Sandbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Critical bootstrap (buildnote/plan/mcp-engineering.md §13.1): redirect Siemens.*
// assembly loading to the registry-registered PublicAPI path BEFORE any Siemens type is touched.
OpennessAssemblyResolver.Register();

// Whitelist registration: pre-register this executable in the TIA Openness whitelist
// to suppress the firewall confirmation dialog on new TiaPortal().
// Requires admin rights for the HKLM write — silently skipped when not elevated.
// Run "Mcp.Engineering.exe --register-whitelist" from an elevated prompt to force it.
if (args.Length > 0 && args[0] == "--register-whitelist")
{
    var ok = WhitelistRegistrar.TryRegister();
    Console.Error.WriteLine(ok
        ? "whitelist: registration succeeded — firewall prompts should no longer appear."
        : "whitelist: registration FAILED — try running as Administrator.");
    if (!ok) Environment.Exit(1);
    Environment.Exit(0);
}
// Opportunistic check-then-register during normal startup: avoids the write when
// the entry is already current, and silently catches the (common) non-admin case.
if (!WhitelistRegistrar.IsRegistered())
{
    if (WhitelistRegistrar.TryRegister())
        Console.Error.WriteLine("whitelist: registered successfully.");
    else
        Console.Error.WriteLine("whitelist: not registered (run elevated with --register-whitelist to suppress TIA Openness firewall prompts).");
}

var builder = Host.CreateApplicationBuilder(args);

// MCP stdio transport: stdout is the JSON-RPC channel — all logging must go to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// Sandbox (agent-sandbox proposal 2026-07-20): tier policy + path jail + audit, loaded once at startup.
var sandboxConfig = SandboxConfig.LoadDefault();
builder.Services.AddSingleton(sandboxConfig);
builder.Services.AddSingleton(new SandboxAudit(sandboxConfig.AuditDirectory, "engineering"));
builder.Services.AddSingleton<EngineeringGuard>();
Console.Error.WriteLine(
    $"sandbox: {sandboxConfig.Policy.Tiers.Count} tools classified from {sandboxConfig.SourceDescription}; " +
    $"roots: {string.Join("; ", sandboxConfig.PathJail.Roots)}");

builder.Services.AddSingleton<TiaV17Adapter>();
builder.Services.AddSingleton<IEngineeringPlatform>(sp => sp.GetRequiredService<TiaV17Adapter>());

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
