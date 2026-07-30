namespace Agent.Chat;

public sealed record BlockInterfaceSummary(
    string BlockId,
    string Kind,
    string Name,
    string? SourceFile,
    string? InstanceDb,
    IReadOnlyList<BlockInterfaceMember> Members,
    IReadOnlyList<BlockCallSite> CallSites,
    IReadOnlyList<BlockNetworkSummary> Networks);

public sealed record BlockInterfaceMember(
    string Name,
    string? Path,
    string? DataType);

public sealed record BlockCallSite(
    string CallerBlock,
    string NetworkId,
    int? NetworkIndex,
    string? SourceFile,
    string LogicStatements);

public sealed record BlockNetworkSummary(
    string NetworkId,
    int? Index,
    string? Language,
    string? LogicStatements);
