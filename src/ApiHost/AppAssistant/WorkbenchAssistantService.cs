using Agent.Workbench;

namespace ApiHost.AppAssistant;

/// <summary>
/// The workbench-scoped Workbench Assistant. It runs on the same <see cref="ApiChatService"/> and
/// <c>AgentLoop</c> as the device chat, under <see cref="ChatScopes.Workbench"/>, so the panel gets
/// the same live MCP tool catalog, error recovery, sandbox and audit trail instead of the Python
/// LangGraph sidecar's four hardcoded read actions (ADR-0005).
/// </summary>
/// <remarks>
/// The panel keeps its own HTTP contract, so this type exists to translate between the panel's
/// <c>progress</c>/<c>state</c>/<c>answer</c> vocabulary and one chat turn. It deliberately does not
/// hold a second prompt stack: the loop's own system prompt and runtime context already carry the
/// workbench, worktree and device.
/// </remarks>
internal sealed class WorkbenchAssistantService(
    WorkbenchApiState state,
    AppAssistantGateway gateway,
    ApiChatService chat)
{
    /// <summary>Workbench context plus the assistant's reply for one panel turn.</summary>
    public sealed record Turn(AppAssistantWorkbenchContext Context, string Answer, string? SessionId);

    /// <summary>
    /// Orientation. Reports observed workbench state without a model call: the context is already
    /// authoritative, and the panel renders the same snapshot directly above this message.
    /// </summary>
    public async Task<Turn> BootstrapAsync(string workbenchId, CancellationToken cancellationToken = default)
    {
        var context = await gateway.GetContextAsync(workbenchId).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new Turn(context, Describe(context), ActiveSessionId());
    }

    /// <summary>Runs one assistant turn for the panel against the selected device.</summary>
    public async Task<Turn> ChatAsync(
        string workbenchId,
        string message,
        Action<string> progress,
        CancellationToken cancellationToken = default)
    {
        var device = ResolveDevice();
        var answer = await chat.RunStreamingAsync(
            device,
            message,
            progress,
            (_, _) => { },
            cancellationToken,
            ChatScopes.Workbench).ConfigureAwait(false);
        var context = await gateway.GetContextAsync(workbenchId).ConfigureAwait(false);
        return new Turn(context, answer, chat.ActiveSessionId(device, ChatScopes.Workbench));
    }

    /// <summary>
    /// The assistant turn is device-scoped, so it needs a selected device. Selecting the worktree
    /// and device stays the user's job in the UI (ADR-0005's accepted non-goal); the panel still
    /// renders workbench context without one and only a turn requires it.
    /// </summary>
    private DeviceContext ResolveDevice()
    {
        var deviceId = state.Selection?.DeviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new AppAssistantGatewayException(
                "DEVICE_SELECTION_REQUIRED",
                "Select a worktree and device in the workbench before asking the Workbench Assistant.");
        return state.Device(deviceId).Context;
    }

    /// <summary>Active session id of the panel's own scope, or null when no device is selected.</summary>
    private string? ActiveSessionId()
    {
        var deviceId = state.Selection?.DeviceId;
        return string.IsNullOrWhiteSpace(deviceId)
            ? null
            : chat.ActiveSessionId(state.Device(deviceId).Context, ChatScopes.Workbench);
    }

    /// <summary>
    /// Short orientation built only from observed state. Facts the panel can see are stated as facts;
    /// nothing is inferred and no action is claimed to have run.
    /// </summary>
    internal static string Describe(AppAssistantWorkbenchContext context)
    {
        var runtime = context.Runtime;
        var worktrees = runtime.Worktrees;
        var focusedId = runtime.Focus.WorktreeId;
        var focused = worktrees.FirstOrDefault(worktree => worktree.WorktreeId == focusedId);

        var lines = new List<string>
        {
            $"Workbench '{context.Name}' is at runtime revision {runtime.WorkbenchRevision} " +
            $"with {worktrees.Count} worktree{(worktrees.Count == 1 ? string.Empty : "s")}.",
        };

        if (focused is not null)
        {
            var todos = focused.TodoCount == 1 ? "1 open todo" : $"{focused.TodoCount} open todos";
            lines.Add(
                $"Selected worktree '{focused.Name}' is on branch '{focused.Branch}' with {todos}; " +
                $"version control reports '{focused.GitStatus}' and validation '{focused.ValidationState}'.");
        }
        else if (worktrees.Count > 0)
        {
            lines.Add("No worktree is selected. Select one to give me a scope to work in.");
        }

        var enabled = runtime.AvailableActions.Where(action => action.Enabled).Select(action => action.Label).ToArray();
        if (enabled.Length > 0)
            lines.Add($"Available actions: {string.Join(", ", enabled)}.");

        lines.Add(focused is null
            ? "Tell me what you want to inspect, or select a worktree and device so I can act."
            : "Ask me about this worktree, or tell me what to do next.");

        return string.Join(' ', lines);
    }
}
