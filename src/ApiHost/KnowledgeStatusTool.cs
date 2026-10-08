using System.Text.Json;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;

/// <summary>
/// The device chat's knowledge-freshness read: whether the knowledge database this conversation's
/// knowledge tools answer from still describes the current PLC source.
///
/// The gap it closes is a live one. After a TIA program change is committed the device knowledge
/// database is stale, but nothing told the model that, and the knowledge tools happily answered from
/// the old content — so a turn reported "your change is not there" from data that predated it. The
/// runtime context now reports the persisted state, and this tool answers it authoritatively: it
/// compares every managed source XML against the hashes the last successful update applied, so it also
/// catches an edit made outside the app, which sets no staleness flag at all (ADR-0012).
///
/// Safety shape:
/// <list type="bullet">
/// <item>read-tier and side-effect free: it hashes source files and reads device metadata, and never
/// writes the database, the device metadata or a projection;</item>
/// <item>the device is the conversation's own validated <see cref="DeviceContext"/>, so the model
/// cannot aim it at another device's database.</item>
/// </list>
/// </summary>
internal sealed class KnowledgeStatusTool(WorkbenchCoordinator coordinator)
{
    public const string ToolName = "knowledge_status";
    private const string ServerName = "workbench";

    /// <summary>Paths listed per category. A rebuilt-from-scratch device can report every source file
    /// as added, so the lists stay bounded and the counts carry the full picture.</summary>
    public const int MaxListedPaths = 50;

    public const string Description =
        "Report whether this device's knowledge database still matches the PLC source before you rely "
        + "on it. Call it whenever the user says they changed, edited, imported, or committed a program "
        + "block or network, and whenever the runtime context reports the knowledge state as stale or "
        + "missing, before the first knowledge-DB query of the turn. state is current, stale, or "
        + "missing. When it is not current, call refresh_knowledge and then query the refreshed "
        + "database; never report a program change as absent, unchanged, or ineffective from a database "
        + "whose freshness you have not checked this turn. Each path list is capped at 50 entries and "
        + "pendingComponentCount is the full count.";

    public static JsonElement InputSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
    });

    /// <summary>The agent-visible spec. <paramref name="device"/> resolves the conversation's device at
    /// call time, so the tool always reads the current context rather than a stale one.</summary>
    public static AgentToolSpec CreateSpec(KnowledgeStatusTool tool, Func<DeviceContext?> device) =>
        new(ToolName, Description, InputSchema, new Caller(tool, device), ServerName);

    public object Read(DeviceContext device) => Project(coordinator.ReadKnowledgeStatus(device));

    /// <summary>The one sentence a reader of the status should act on. Shared with the runtime
    /// context so the model is told the same thing whether it asked or was told.</summary>
    public static string Advisory(DeviceKnowledgeStatus status) =>
        status.State switch
        {
            DeviceKnowledgeStatus.CurrentState =>
                "The knowledge database matches the PLC source; query it directly.",
            DeviceKnowledgeStatus.MissingState =>
                "No knowledge database exists for this device yet. Call refresh_knowledge to build it, "
                + "then query it.",
            _ =>
                "The knowledge database is behind the PLC source. Call refresh_knowledge, then answer "
                + "from the refreshed database; do not present the old content as the current program.",
        };

    private static object Project(DeviceKnowledgeStatus status) => new
    {
        state = status.State,
        dbPath = status.DbPath,
        updatedAt = status.UpdatedAt,
        flaggedStale = status.FlaggedStale,
        baselineStale = status.BaselineStale,
        requiresRebuild = status.RequiresRebuild,
        pendingComponentCount = status.PendingComponentCount,
        changedPaths = Cap(status.ChangedPaths),
        addedPaths = Cap(status.AddedPaths),
        removedPaths = Cap(status.RemovedPaths),
        listedPathLimit = MaxListedPaths,
        advisory = Advisory(status),
    };

    private static string[] Cap(IReadOnlyList<string> paths) =>
        paths.Count <= MaxListedPaths ? paths.ToArray() : paths.Take(MaxListedPaths).ToArray();

    /// <summary>Dispatches the single read, bound to the conversation's current device.</summary>
    private sealed class Caller(KnowledgeStatusTool tool, Func<DeviceContext?> device) : IMcpToolCaller
    {
        public Task<T> CallAsync<T>(string name, object args, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(name, ToolName, StringComparison.Ordinal))
                throw new KeyNotFoundException($"Tool '{name}' is not exposed to the agent.");
            var current = device()
                ?? throw new ToolCallException("DEVICE_SELECTION_REQUIRED",
                    $"'{ToolName}' needs a selected device.",
                    "Select a registered device, then call the tool again.");
            var result = tool.Read(current);
            return Task.FromResult(JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result))!);
        }
    }
}
