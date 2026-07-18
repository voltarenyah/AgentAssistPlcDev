namespace Contracts.Engineering;

/// <summary>
/// connect input (mcp-engineering.md §4.4). Exactly one of SessionId (attach to a running
/// TIA instance) or ProjectPath (open a project in a new portal process) must be set.
/// </summary>
public sealed class ConnectOptions
{
    /// <summary>TIA process id (from list_sessions) → attach mode.</summary>
    public int? SessionId { get; set; }

    /// <summary>.ap17 file path → open mode (headless or visible).</summary>
    public string? ProjectPath { get; set; }

    /// <summary>Open mode only: show the TIA UI. Ignored in attach mode (inherits the running session's mode).</summary>
    public bool WithUI { get; set; }

    /// <summary>Max wait for Openness startup. Headless portal start takes ~10–30s.</summary>
    public int TimeoutSeconds { get; set; } = 60;
}
