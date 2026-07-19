using System.IO;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace App.Mcp;

/// <summary>
/// One MCP server child process over stdio (buildnote/plan/app.md §2.7).
/// The server's stderr lines are forwarded via <see cref="StderrLine"/> (stdout is the JSON-RPC channel).
/// </summary>
public sealed class McpServerConnection : IMcpToolCaller, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string serverName;
    private readonly string exePath;
    private McpClient? client;

    public McpServerConnection(string serverName, string exePath)
    {
        this.serverName = serverName;
        this.exePath = exePath;
    }

    /// <summary>Raised once per stderr line received from the server process.</summary>
    public event Action<string>? StderrLine;

    public bool IsRunning => client != null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException(
                $"MCP server '{serverName}' exe not found: {exePath}. Build the server project first, or set the override in %APPDATA%\\PlcAiAssistant\\config.json.",
                exePath);
        }

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = exePath,
            Name = serverName,
            StandardErrorLines = line => StderrLine?.Invoke($"[{serverName}] {line}"),
        });
        client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    public async Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
    {
        if (client == null)
        {
            throw new InvalidOperationException($"MCP server '{serverName}' is not started.");
        }

        var result = await client.CallToolAsync(tool, ToArguments(args), cancellationToken: cancellationToken);
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;
        if (result.IsError == true)
        {
            throw ParseError(text);
        }

        return JsonSerializer.Deserialize<T>(text, Json)
            ?? throw new InvalidOperationException($"Tool '{tool}' on server '{serverName}' returned an empty result.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (client != null)
            {
                // Disposing the client shuts down the stdio session transport and the child process.
                await client.DisposeAsync();
            }
        }
        catch
        {
            // best effort; the child process dies with the app anyway
        }

        client = null;
    }

    private static IReadOnlyDictionary<string, object?> ToArguments(object args)
    {
        var element = JsonSerializer.SerializeToElement(args, Json);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?>();
        }

        return element.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value);
    }

    private static ToolCallException ParseError(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var error = document.RootElement.GetProperty("error");
            return new ToolCallException(
                error.GetProperty("code").GetString() ?? "UNEXPECTED_ERROR",
                error.GetProperty("message").GetString() ?? text,
                error.TryGetProperty("remediation", out var remediation) ? remediation.GetString() : null);
        }
        catch (JsonException)
        {
            return new ToolCallException("UNEXPECTED_ERROR", text, null);
        }
    }
}
