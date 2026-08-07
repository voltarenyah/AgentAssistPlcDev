namespace Agent.Workbench;

public sealed record PendingMasterSource(
    string RelativePath,
    string ComparisonId,
    string MasterHeadSha,
    string TiaFingerprint,
    string CopiedFileFingerprint);

public sealed record PendingMasterSynchronization(
    string SchemaVersion,
    string WorktreeId,
    IReadOnlyList<PendingMasterSource> Sources);

/// <summary>Applies the worktree branch rule shared by UI and MCP source edits.</summary>
public sealed class WorkbenchWritePolicy(AtomicJsonStore store)
{
    public const string PendingSchemaVersion = "1.0";
    public const string PendingFileName = "pending-master-sync.json";

    public PendingMasterSynchronization ReadPending(string worktreeRoot, string worktreeId)
    {
        var path = Path.Combine(worktreeRoot, ".automation", PendingFileName);
        var pending = store.TryRead<PendingMasterSynchronization>(path);
        return pending is null
            ? new PendingMasterSynchronization(PendingSchemaVersion, worktreeId, Array.Empty<PendingMasterSource>())
            : pending;
    }

    public void WritePending(string worktreeRoot, PendingMasterSynchronization pending)
    {
        var path = Path.Combine(worktreeRoot, ".automation", PendingFileName);
        if (pending.Sources.Count == 0)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        store.Write(path, pending);
    }

    /// <summary>Validates worktree metadata for ordinary PLC source edits. Direct edits are
    /// allowed on any worktree including master (vc-restructure decision: MASTER_EDIT_NOT_ALLOWED
    /// disabled); master commits of direct edits are committed as unlabeled savepoints, while
    /// TIA-accepted files keep their staleness checks via the pending-authorization records.</summary>
    public void RequireFeatureEdit(DeviceContext context)
    {
        var metadataPath = Path.Combine(context.WorktreeRoot, "worktree.json");
        var metadata = store.TryRead<WorktreeMetadata>(metadataPath);
        if (metadata is null)
        {
            throw new WorkbenchLifecycleException(
                "WORKTREE_METADATA_REQUIRED",
                "Ordinary PLC source edits require valid worktree metadata.");
        }
    }
}
