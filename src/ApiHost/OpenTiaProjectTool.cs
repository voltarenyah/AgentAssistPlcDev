using System.Text.Json;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;

/// <summary>
/// The device chat's action for showing the conversation's worktree project in TIA Portal
/// (<c>open_tia_project</c>). It is registered in-process because resolving the worktree's
/// <b>registered</b> project and reusing a running TIA instance are knowledge only this process holds:
/// the engineering MCP server sees a project path, never the workbench registration, and the model
/// must not have to guess the path or the session.
///
/// Safety shape:
/// <list type="bullet">
/// <item>takes no arguments: the worktree and its registered project come from the conversation's own
/// validated <see cref="DeviceContext"/>, so the model cannot aim the action at another worktree or an
/// arbitrary path;</item>
/// <item>write-tier, not destructive: it opens TIA, it never saves, imports, compiles or overwrites
/// user work, so no approval card interrupts the request;</item>
/// <item>the open itself is <see cref="WorkbenchCoordinator.ShowWorktreeProjectInTiaAsync"/>, so an
/// already-running TIA Portal that shows this project is attached instead of a second TIA being
/// started — and the reported project is the one TIA actually reports, never a constructed path.</item>
/// </list>
/// </summary>
internal sealed class OpenTiaProjectTool(WorkbenchApiState state, WorkbenchCoordinator coordinator)
{
    public const string ToolName = "open_tia_project";
    private const string ServerName = "workbench";

    public const string Description =
        "Open this worktree's registered TIA project in TIA Portal with its user interface, so live TIA "
        + "work (import, compile, compare, online status) can continue. Takes no arguments: the worktree "
        + "and its project come from the current conversation context. A running TIA Portal that already "
        + "shows this project is attached instead of a second TIA Portal being started. Opening TIA can "
        + "take a minute or two. Use this instead of asking the user to open the project in TIA Portal. "
        + "It is not needed for offline knowledge questions.";

    /// <summary>No arguments by design: everything comes from the selected device context.</summary>
    public static JsonElement InputSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
    });

    /// <summary>The agent-visible spec. <paramref name="device"/> resolves the conversation's device at
    /// call time, so the tool always acts on the current context rather than a stale one.</summary>
    public static AgentToolSpec CreateSpec(OpenTiaProjectTool tool, Func<DeviceContext?> device) =>
        new(ToolName, Description, InputSchema, new Caller(tool, device), ServerName);

    public async Task<object> OpenAsync(
        DeviceContext device,
        CancellationToken cancellationToken = default)
    {
        var workbench = state.Workbench(device.WorkbenchId);
        if (!workbench.Worktrees.Any(item => item.WorktreeId == device.WorktreeId))
        {
            throw new ToolCallException("WORKTREE_NOT_FOUND",
                $"Worktree '{device.WorktreeId}' was not found in workbench '{device.WorkbenchId}'.", null);
        }

        var result = await coordinator.ShowWorktreeProjectInTiaAsync(device, cancellationToken)
            .ConfigureAwait(false);
        return new
        {
            opened = true,
            projectName = result.ProjectName,
            projectPath = result.ProjectPath,
            reusedRunningSession = result.ReusedRunningSession,
            withUI = result.WithUI,
        };
    }

    /// <summary>Dispatches the single action, bound to the conversation's current device.</summary>
    private sealed class Caller(OpenTiaProjectTool tool, Func<DeviceContext?> device) : IMcpToolCaller
    {
        public async Task<T> CallAsync<T>(string name, object args, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(name, ToolName, StringComparison.Ordinal))
                throw new KeyNotFoundException($"Tool '{name}' is not exposed to the agent.");
            var current = device()
                ?? throw new ToolCallException("DEVICE_SELECTION_REQUIRED",
                    $"'{ToolName}' needs a selected device.", "Select a registered device, then call the tool again.");
            var result = await tool.OpenAsync(current, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result))!;
        }
    }
}
