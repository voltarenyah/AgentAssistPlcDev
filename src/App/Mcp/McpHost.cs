namespace App.Mcp;

/// <summary>
/// Owns the MCP server child processes for the app session (engineering + knowledge).
/// When the Agent (Phase 2 step 6) lands, Mcp/ + Workflows/ move to a shared net8 library (buildnote/plan/app.md §2.3).
/// </summary>
public sealed class McpHost : IAsyncDisposable
{
    public McpHost(AppSettings settings)
    {
        Engineering = new McpServerConnection("engineering", settings.EngineeringServerPath);
        Knowledge = new McpServerConnection("knowledge", settings.KnowledgeServerPath);
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
