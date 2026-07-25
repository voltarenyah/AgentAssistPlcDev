using Contracts.Sandbox;
using Mcp.SourceEditor.Xml;
using Mcp.SourceEditor.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
var sandbox = SandboxConfig.LoadDefault();
builder.Services.AddSingleton(sandbox.PathJail);
builder.Services.AddSingleton<SourceEditorService>();
builder.Services.AddSingleton<SourceEditorTools>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();
