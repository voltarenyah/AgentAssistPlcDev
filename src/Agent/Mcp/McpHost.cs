namespace Agent.Mcp;

/// <summary>
/// Owns the MCP server child processes for the app session.
/// Lives in the shared Agent library per buildnote/plan/app.md §2.3.
/// </summary>
public sealed class McpHost : IAsyncDisposable
{
    public McpHost(string engineeringServerPath, string knowledgeServerPath)
        : this(engineeringServerPath, knowledgeServerPath, null, null)
    {
    }

    public McpHost(string engineeringServerPath, string knowledgeServerPath, string? versionControlServerPath,
        string? sourceEditorServerPath = null,
        IReadOnlyDictionary<string, string?>? sandboxEnvironment = null)
    {
        Engineering = new McpServerConnection("engineering", engineeringServerPath, sandboxEnvironment);
        Knowledge = new McpServerConnection("knowledge", knowledgeServerPath);
        Engineering.StderrLine += line => ServerLog?.Invoke(line);
        Knowledge.StderrLine += line => ServerLog?.Invoke(line);

        if (!string.IsNullOrWhiteSpace(versionControlServerPath))
        {
            VersionControl = new McpServerConnection("versioncontrol", versionControlServerPath);
            VersionControl.StderrLine += line => ServerLog?.Invoke(line);
        }
        if (!string.IsNullOrWhiteSpace(sourceEditorServerPath))
        {
            SourceEditor = new McpServerConnection("sourceeditor", sourceEditorServerPath, sandboxEnvironment);
            SourceEditor.StderrLine += line => ServerLog?.Invoke(line);
        }
    }

    /// <summary>Stderr lines from all hosted servers, prefixed with the server name.</summary>
    public event Action<string>? ServerLog;

    public McpServerConnection Engineering { get; }
    public McpServerConnection Knowledge { get; }
    public McpServerConnection? VersionControl { get; }
    public McpServerConnection? SourceEditor { get; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Engineering.StartAsync(cancellationToken);
        await Knowledge.StartAsync(cancellationToken);
        if (VersionControl != null)
        {
            await VersionControl.StartAsync(cancellationToken);
        }
        if (SourceEditor != null) await SourceEditor.StartAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (SourceEditor != null) await SourceEditor.DisposeAsync();
        if (VersionControl != null)
        {
            await VersionControl.DisposeAsync();
        }
        await Knowledge.DisposeAsync();
        await Engineering.DisposeAsync();
    }
}
