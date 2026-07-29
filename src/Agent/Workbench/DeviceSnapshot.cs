namespace Agent.Workbench;

public sealed record DeviceKnowledgeSnapshot(string State, string? UpdatedAt);

public sealed record OfflineBlockInfo(
    string Id,
    string Name,
    int? Number,
    string BlockType,
    string? ProgrammingLanguage,
    string? GroupPath,
    string RelativePath,
    bool Modified);

public sealed record DeviceSnapshot(
    string WorkbenchId,
    string WorktreeId,
    string DeviceId,
    string PlcName,
    string EngineeringIdentity,
    string ExportedSourceRoot,
    string ModifiedSourceRoot,
    string KnowledgeDbPath,
    DeviceKnowledgeSnapshot Knowledge,
    IReadOnlyList<OfflineBlockInfo> Blocks,
    int OverlayCount,
    IReadOnlyList<string> Diagnostics);

public sealed class DeviceSnapshotReader
{
    public DeviceSnapshot Read(DeviceContext context, DeviceMetadata metadata)
    {
        var state = !File.Exists(context.KnowledgeDbPath)
            ? "missing"
            : metadata.Knowledge.Stale || metadata.Knowledge.BaselineStale
                ? "stale"
                : "current";

        return new DeviceSnapshot(
            context.WorkbenchId,
            context.WorktreeId,
            context.DeviceId,
            metadata.PlcName,
            metadata.EngineeringIdentity,
            context.ExportedSourceRoot,
            context.ModifiedSourceRoot,
            context.KnowledgeDbPath,
            new DeviceKnowledgeSnapshot(state, metadata.Knowledge.UpdatedAt),
            [],
            0,
            []);
    }
}
