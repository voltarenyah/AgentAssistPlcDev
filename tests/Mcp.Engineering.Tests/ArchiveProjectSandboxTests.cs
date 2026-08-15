using System;
using System.IO;
using Contracts;
using Contracts.Engineering;
using Contracts.Sandbox;
using Mcp.Engineering.Sandbox;
using Mcp.Engineering.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class ArchiveProjectSandboxTests : IDisposable
{
    private readonly string sandboxRoot;

    public ArchiveProjectSandboxTests()
    {
        sandboxRoot = Path.Combine(Path.GetTempPath(), "archive-project-jail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandboxRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(sandboxRoot))
        {
            Directory.Delete(sandboxRoot, recursive: true);
        }
    }

    [Fact]
    public void ArchiveProjectJailsOnlyTheExportDirectoryAndAllowsAFileName()
    {
        var allowed = Directory.CreateDirectory(Path.Combine(sandboxRoot, "allowed"));
        var configPath = Path.Combine(sandboxRoot, "sandbox.json");
        File.WriteAllText(configPath,
            "{\"allowedRoots\": [\"" + allowed.FullName.Replace("\\", "\\\\") + "\"], " +
            "\"auditDirectory\": \"" + Path.Combine(sandboxRoot, "audit").Replace("\\", "\\\\") + "\"}");
        var config = SandboxConfig.Load(configPath);
        var recorder = new RecordingEngineeringPlatform();
        var tools = new EngineeringTools(
            recorder,
            new EngineeringGuard(config, new SandboxAudit(config.AuditDirectory, "archive-project-test")));

        var result = tools.ArchiveProject(allowed.FullName, "ArchiveProject.zap17");

        Assert.False(result.IsError);
        Assert.Equal("ArchiveProject.zap17", recorder.ArchiveName);
        Assert.Equal(allowed.FullName, recorder.TargetDirectory);
    }

    private sealed class RecordingEngineeringPlatform : IEngineeringPlatform
    {
        public string? TargetDirectory { get; private set; }
        public string? ArchiveName { get; private set; }

        public EnvCheckResult CheckEnvironment() => new();
        public SessionInfo[] ListSessions() => Array.Empty<SessionInfo>();
        public ConnectionInfo Connect(ConnectOptions options) => new();
        public DisconnectResult Disconnect() => new();
        public void SaveProject() { }
        public ProjectCreateResult CreateProject(DirectoryInfo targetDirectory, string projectName) => new();

        public ProjectArchiveResult ArchiveProject(DirectoryInfo targetDirectory, string archiveName, string archivationMode = "compressed")
        {
            TargetDirectory = targetDirectory.FullName;
            ArchiveName = archiveName;
            return new ProjectArchiveResult
            {
                ProjectName = "TestProject",
                ArchivePath = Path.Combine(TargetDirectory, ArchiveName),
            };
        }

        public ProjectRetrieveResult RetrieveProject(FileInfo archivePath, DirectoryInfo targetDirectory, bool upgrade = false, string openMode = "primary") => new();
        public string SaveProjectAs(DirectoryInfo targetDirectory) => targetDirectory.FullName;
        public ProjectInfo GetProjectInfo() => new();
        public ProjectCapabilities GetProjectCapabilities() => new();
        public BlockInfo[] ListBlocks(string? plcName) => Array.Empty<BlockInfo>();
        public PlcChecksumInfo[] GetPlcChecksums(string? plcName = null) => Array.Empty<PlcChecksumInfo>();
        public ExportResult ExportBlock(string blockName, string outputDir) => new();
        public ExportResult ExportSourceObject(string name, string category, string outputDir, string? plcName = null) => new();
        public ExportResult[] ExportAllBlocks(string outputDir, IProgress<EngineeringProgress>? progress = null) => Array.Empty<ExportResult>();
        public ExportResult[] ExportTagTables(string outputDir, string? plcName, IProgress<EngineeringProgress>? progress = null) => Array.Empty<ExportResult>();
        public ExportResult[] ExportUdts(string outputDir, string? plcName, IProgress<EngineeringProgress>? progress = null) => Array.Empty<ExportResult>();
        public SyncResult[] SyncExport(string outputDir, string? plcName, IProgress<EngineeringProgress>? progress = null) => Array.Empty<SyncResult>();
        public SyncResult[] RebuildExport(string outputDir, string? plcName = null, IProgress<EngineeringProgress>? progress = null) => Array.Empty<SyncResult>();
        public HardwareExportResult[] ExportHardwareConfiguration(string outputDir, bool includeDeviceExports = true, IProgress<EngineeringProgress>? progress = null) => Array.Empty<HardwareExportResult>();
        public void CloseSession(int sessionId) { }
        public ContextStatusResult[] GetContextStatus(string outputDir, string? plcName) => Array.Empty<ContextStatusResult>();
        public ContextCompareResult[] CompareContext(string outputDir, string? plcName) => Array.Empty<ContextCompareResult>();
        public ImportResult ImportBlock(string blockName, string xmlFilePath, string? plcName = null) => new();
        public HardwareImportResult ImportHardwareConfiguration(string amlFilePath, string? logFilePath = null, HardwareImportConflictPolicy conflictPolicy = HardwareImportConflictPolicy.MoveToParkingLot) => new();
        public BlockInfo CreateBlock(string blockName, string blockType, int number = 0, string? programmingLanguage = null, string? instanceOfName = null, string? plcName = null) => new();
        public BlockMutationResult DeleteBlock(string blockName, string? plcName = null) => new();
        public SourceObjectImportResult ImportSourceObject(string relativePath, string xmlFilePath, string? plcName = null) => new();
        public CompileResult CompileBlock(string blockName, string? plcName = null) => new();
        public CompileResult CompilePlc(string? plcName = null) => new();
        public void OpenBlockInEditor(string blockName) { }
        public OpenInEditorResult OpenSourceObjectInEditor(string name, string category, string? plcName = null) => new();
        public void Dispose() { }
    }
}
