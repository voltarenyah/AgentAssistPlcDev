namespace Agent.Mcp;

/// <summary>
/// Single seam for all MCP tool traffic (buildnote/plan/app.md §2.7).
/// Workflows and view models depend only on this interface; unit tests use a scripted fake.
/// </summary>
public interface IMcpToolCaller
{
    /// <summary>Calls one MCP tool and deserializes its JSON result; throws <see cref="ToolCallException"/> on isError results.</summary>
    Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default);
}
