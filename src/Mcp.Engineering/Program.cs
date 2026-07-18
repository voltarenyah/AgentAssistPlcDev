using Contracts;
using Mcp.Engineering.Adapter;
using Mcp.Engineering.Openness;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Critical bootstrap (buildnote/plan/mcp-engineering.md §13.1): redirect Siemens.*
// assembly loading to the registry-registered PublicAPI path BEFORE any Siemens type is touched.
OpennessAssemblyResolver.Register();

var builder = Host.CreateApplicationBuilder(args);

// MCP stdio transport: stdout is the JSON-RPC channel — all logging must go to stderr.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<TiaV17Adapter>();
builder.Services.AddSingleton<IEngineeringPlatform>(sp => sp.GetRequiredService<TiaV17Adapter>());

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
