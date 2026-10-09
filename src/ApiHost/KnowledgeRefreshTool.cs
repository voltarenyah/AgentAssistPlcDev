using System.Text.Json;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Contracts.Knowledge;

/// <summary>
/// The device chat's knowledge refresh: bring this device's knowledge database up to date with its PLC
/// source, so the knowledge tools answer from the current program.
///
/// It replaces the two raw knowledge tools the model used to reach for. Calling
/// <c>ingest_source</c>/<c>update_components</c> from the chat updated the database but bypassed the
/// device's applied-hash bookkeeping, which is what tells the app — and every later turn — whether the
/// database is current; the device then stayed flagged stale even though the graph had changed. This
/// tool routes through the coordinator's guarded knowledge operations, so the applied hashes, the
/// staleness flags and the projected device facts move together, and the host decides between a
/// partial component update and a full rebuild.
///
/// Safety shape:
/// <list type="bullet">
/// <item>the device is the conversation's own validated <see cref="DeviceContext"/>, so the model
/// cannot refresh another device's database;</item>
/// <item>it is classified write, not destructive: it rebuilds a derived, Git-ignored database and
/// writes no PLC source, no TIA state and no Git state, so it needs no approval card;</item>
/// <item>the coordinator's device operation lock serializes it against export, import, edit and other
/// knowledge work for the same device.</item>
/// </list>
/// </summary>
internal sealed class KnowledgeRefreshTool(WorkbenchCoordinator coordinator)
{
    public const string ToolName = "refresh_knowledge";
    private const string ServerName = "workbench";

    /// <summary>Component names echoed back at most; the count is always the full one.</summary>
    public const int MaxListedComponents = 50;

    public const string Description =
        "Bring this device's knowledge database up to date with the PLC source, then answer from the "
        + "refreshed database. Use it after the user changed, imported or committed a program, and "
        + "whenever knowledge_status reports anything other than current — call knowledge_status first "
        + "when you have not already this turn. The host decides between a partial component update and "
        + "a full rebuild, so never call ingest_source or update_components for this device: they "
        + "bypass the applied-hash bookkeeping that tells the app and later turns whether the database "
        + "is current. It rebuilds a local derived database and changes no PLC source and no Git state, "
        + "so it needs no approval; a full rebuild of a large project can take minutes. The result "
        + "reports the state after the refresh: when it is still not current, say so instead of "
        + "presenting the old content as the current program.";

    public static JsonElement InputSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
    });

    /// <summary>The agent-visible spec. <paramref name="device"/> resolves the conversation's device at
    /// call time, so the tool always acts on the current context rather than a stale one.</summary>
    public static AgentToolSpec CreateSpec(KnowledgeRefreshTool tool, Func<DeviceContext?> device) =>
        new(ToolName, Description, InputSchema, new Caller(tool, device), ServerName);

    public async Task<object> RefreshAsync(
        DeviceContext device,
        CancellationToken cancellationToken = default)
    {
        var before = coordinator.ReadKnowledgeStatus(device);
        if (before.IsCurrent)
        {
            return Project(
                KnowledgeRefreshMode.AlreadyCurrent,
                before,
                coordinator.ReadKnowledgeStatus(device),
                Array.Empty<string>(),
                Array.Empty<string>());
        }

        // A missing database, a stale baseline, a removed component or a database with no applied
        // hashes can only be repaired by a full rebuild; everything else is the partial update.
        var rebuild = before.RequiresRebuild;
        var result = rebuild
            ? await coordinator.RebuildKnowledgeAsync(device, cancellationToken).ConfigureAwait(false)
            : await coordinator.UpdateKnowledgeAsync(device, cancellationToken).ConfigureAwait(false);
        var after = coordinator.ReadKnowledgeStatus(device);
        var components = result.UpdatedComponents.Length <= MaxListedComponents
            ? result.UpdatedComponents
            : result.UpdatedComponents.Take(MaxListedComponents).ToArray();
        return Project(
            rebuild ? KnowledgeRefreshMode.Rebuild : KnowledgeRefreshMode.Update,
            before,
            after,
            components,
            result.Warnings);
    }

    private static object Project(
        KnowledgeRefreshMode mode,
        DeviceKnowledgeStatus before,
        DeviceKnowledgeStatus after,
        IReadOnlyList<string> components,
        IReadOnlyList<string> warnings) => new
    {
        state = after.State,
        mode = mode.ToString().ToLowerInvariant(),
        stateBefore = before.State,
        dbPath = after.DbPath,
        updatedAt = after.UpdatedAt,
        updatedComponentCount = components.Count,
        updatedComponents = components,
        pendingComponentCount = after.PendingComponentCount,
        warnings,
        advisory = KnowledgeStatusTool.Advisory(after),
    };

    /// <summary>What the refresh did, in the words the model reports back to the user.</summary>
    private enum KnowledgeRefreshMode
    {
        /// <summary>The database already matched the source; nothing was written.</summary>
        AlreadyCurrent,

        /// <summary>The changed components were replaced.</summary>
        Update,

        /// <summary>The whole database was rebuilt from the source tree.</summary>
        Rebuild,
    }

    /// <summary>Dispatches the single refresh, bound to the conversation's current device.</summary>
    private sealed class Caller(KnowledgeRefreshTool tool, Func<DeviceContext?> device) : IMcpToolCaller
    {
        public async Task<T> CallAsync<T>(
            string name,
            object args,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(name, ToolName, StringComparison.Ordinal))
                throw new KeyNotFoundException($"Tool '{name}' is not exposed to the agent.");
            var current = device()
                ?? throw new ToolCallException("DEVICE_SELECTION_REQUIRED",
                    $"'{ToolName}' needs a selected device.",
                    "Select a registered device, then call the tool again.");
            var result = await tool.RefreshAsync(current, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result))!;
        }
    }
}
