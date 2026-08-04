namespace Agent.Workbench;

/// <summary>Applies the worktree branch rule shared by UI and MCP source edits.</summary>
public sealed class WorkbenchWritePolicy(AtomicJsonStore store)
{
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
        if (!string.Equals(metadata.Branch, "master", StringComparison.OrdinalIgnoreCase))
            return;

        throw new WorkbenchLifecycleException(
            "MASTER_EDIT_NOT_ALLOWED",
            "Ordinary PLC source edits are only allowed on a feature worktree.");
    }
}
