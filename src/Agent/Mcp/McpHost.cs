namespace Agent.Mcp;

/// <summary>
/// Owns the MCP server child processes for the app session (engineering + knowledge).
/// Lives in the shared Agent library per buildnote/plan/app.md §2.3 (moved from App when step 6 landed).
/// </summary>
public sealed class McpHost : IAsyncDisposable
{
    public McpHost(string engineeringServerPath, string knowledgeServerPath)
    {
        Engineering = new McpServerConnection("engineering", engineeringServerPath);
        Knowledge = new McpServerConnection("knowledge", knowledgeServerPath);
        Engineering.StderrLine += line => ServerLog?.Invoke(line);
        Knowledge.StderrLine += line => ServerLog?.Invoke(line);
    }

    /// <summary>Stderr lines from all hosted servers, prefixed with the server name.</summary>
    public event Action<string>? ServerLog;

    public McpServerConnection Engineering { get; }

    public McpServerConnection Knowledge { get; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Engineering.StartAsync(cancellationToken);
        await Knowledge.StartAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Engineering.DisposeAsync();
        await Knowledge.DisposeAsync();
    }
}
