using Contracts.Engineering;

namespace Contracts;

/// <summary>
/// Version-agnostic contract between an engineering-software adapter (TIA V17 today;
/// Rockwell L5X, TIA V18+ later) and the MCP server hosting it.
/// Tool surface: buildnote/plan/mcp-engineering.md §3 (Phase 1 = 12 tools).
/// </summary>
public interface IEngineeringPlatform : IDisposable
{
    EnvCheckResult CheckEnvironment();
    SessionInfo[] ListSessions();
    ConnectionInfo Connect(ConnectOptions options);
    DisconnectResult Disconnect();

    /// <summary>Explicit save — the ONLY operation that persists the project (locked decision §1.1).</summary>
    void SaveProject();

    ProjectInfo GetProjectInfo();
    BlockInfo[] ListBlocks(string? plcName);

    ExportResult ExportBlock(string blockName, string outputDir);
    ExportResult[] ExportAllBlocks(string outputDir);
    ImportResult ImportBlock(string blockName, string xmlFilePath);

    CompileResult CompileBlock(string blockName);
    CompileResult CompilePlc();
}
