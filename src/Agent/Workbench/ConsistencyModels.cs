namespace Agent.Workbench;

public sealed record SourceObjectSnapshot(
    string Identity,
    string RelativePath,
    string Category,
    string Name,
    string Sha256,
    long Length);

public sealed record DeviceSourceSnapshot(
    string DeviceId,
    string PlcName,
    string ProjectIdentity,
    string ProjectChecksum,
    IReadOnlyList<SourceObjectSnapshot> Objects);
