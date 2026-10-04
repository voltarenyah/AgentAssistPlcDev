using System.Text.Json;
using Agent.Mcp;
using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;

/// <summary>
/// Records the <c>git_commit</c> evidence of a commit made by a raw gateway route (AC-006).
/// <c>POST /api/vc/commit</c>, <c>POST …/devices/{device}/vc/commit</c>, the
/// <c>vc_commit_selected</c> fallback and <c>POST /api/vc/checkout</c> call the version-control tool
/// directly, so they never reach <see cref="WorkbenchCoordinator"/>'s evidence indexing
/// (<c>IndexGraphEvidence</c>): the commit they made had no commit node, and its commit→source-object
/// read answered 404 — the defect ADR-0011 records for a workbench whose graph holds no commit nodes.
/// </summary>
/// <remarks>
/// Both helpers are best effort by design. The Git operation has already succeeded when they run, and
/// every one of these routes has a response shape the Studio consumes, so a graph that cannot be
/// written may not turn a successful commit into an error. The warning the indexer returns is
/// deliberately dropped for that reason; a repair pass can rebuild the entity from the commit id.
/// </remarks>
public static class RawGatewayCommitEvidence
{
    /// <summary>Indexes a commit whose result the caller already holds, with the commit's own file list.</summary>
    public static void IndexCommit(
        EngineeringGraphEvidenceIndexerProvider indexer,
        WorkbenchMetadata workbench,
        string worktreeId,
        JsonElement commit)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        // ADR-0012 item 2: a raw gateway commit is a write point. It invalidates first and
        // unconditionally, so a result without a readable commit id still marks the worktree's
        // projected facts stale.
        InvalidateWorktreeProjection(workbench, worktreeId);
        if (JsonString(commit, "sha") is not { } sha) return;
        // A commit result with no file list still records the node: the commit happened, and an empty
        // object list is the honest evidence for it.
        _ = indexer.TryIndexCommit(workbench, worktreeId, sha, Files(commit, "files"));
    }

    /// <summary>
    /// ADR-0012 item 2 for a raw gateway route: a commit or a branch switch changed the worktree's Git
    /// state, so every device whose stored facts came from that worktree is marked for re-projection.
    /// Best effort, exactly like the evidence indexing beside it: the Git operation already succeeded.
    /// </summary>
    public static void InvalidateWorktreeProjection(WorkbenchMetadata workbench, string worktreeId)
    {
        if (string.IsNullOrWhiteSpace(worktreeId)) return;
        try
        {
            using var store = new EngineeringGraphStore(workbench.RootPath);
            var graph = new EngineeringGraphService(store, workbench.WorkbenchId,
                id => workbench.Worktrees.Any(item => item.WorktreeId == id));
            graph.InvalidateWorktreeProjections(worktreeId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The route's own operation already succeeded; the selection boundary's digest check is the
            // second line of defence for the same change.
        }
    }

    /// <summary>
    /// Indexes the commit a checkout moved HEAD to. The checkout names the commit but not its files, so
    /// the file list is read from the target's own log; when the log does not name that commit the node
    /// is still registered.
    /// </summary>
    public static async Task IndexCheckedOutHeadAsync(
        EngineeringGraphEvidenceIndexerProvider indexer,
        IMcpToolCaller versionControl,
        WorkbenchMetadata workbench,
        string worktreeId,
        string worktreeRoot,
        JsonElement checkout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        // ADR-0012 item 2: a branch switch moves the whole working tree the projected facts were read
        // from, and it invalidates whether or not the checkout names a commit.
        InvalidateWorktreeProjection(workbench, worktreeId);
        if (JsonString(checkout, "sha") is not { } sha) return;
        IReadOnlyList<string> files = [];
        try
        {
            var log = await versionControl.CallAsync<ConsistencyLogResult>(
                "vc_log", new { repoPath = worktreeRoot, maxCount = 1 }, cancellationToken).ConfigureAwait(false);
            var head = log.Commits.FirstOrDefault();
            if (head is not null && string.Equals(head.Sha, sha, StringComparison.OrdinalIgnoreCase))
                files = head.Files;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The branch switch already succeeded; the node is registered below without object edges.
        }

        _ = indexer.TryIndexCommit(workbench, worktreeId, sha, files);
    }

    private static IReadOnlyList<string> Files(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object
            || owner.EnumerateObject().FirstOrDefault(property =>
                string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) is not { Value.ValueKind: JsonValueKind.Array } found)
        {
            return [];
        }

        return found.Value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }

    /// <summary>One string property of a version-control result, whichever casing the server used.</summary>
    private static string? JsonString(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object
        && owner.EnumerateObject().FirstOrDefault(property =>
            string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } found
        && !string.IsNullOrWhiteSpace(found.Value.GetString())
            ? found.Value.GetString()
            : null;
}
