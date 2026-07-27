using System.Diagnostics;
using Agent.Mcp;
using Agent.Workbench;
using Contracts.Engineering;

namespace Agent.Workflows;

/// <summary>
/// Stages a selected device export for review. It never reconciles staging into the tracked
/// baseline; that requires a separate approved coordinator operation.
/// </summary>
public sealed class ReadProjectContextWorkflow
{
    private readonly IMcpToolCaller engineering;
    private readonly IProgress<string>? progress;
    private readonly SafeDeviceExportStager stager;

    public ReadProjectContextWorkflow(
        IMcpToolCaller engineering,
        IMcpToolCaller knowledge,
        IProgress<string>? progress = null,
        Func<string, bool>? fileExists = null,
        SafeDeviceExportStager? stager = null)
    {
        this.engineering = engineering;
        this.progress = progress;
        _ = knowledge ?? throw new ArgumentNullException(nameof(knowledge));
        _ = fileExists; // Retained for source compatibility; this workflow no longer mutates knowledge.
        this.stager = stager ?? new SafeDeviceExportStager(engineering);
    }

    public async Task<ReadProjectContextResult> RunAsync(
        DeviceContext device,
        string plcName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(plcName))
        {
            throw new ArgumentException("A selected PLC name is required.", nameof(plcName));
        }

        var info = await Timed(
            "Reading project info",
            () => engineering.CallAsync<ProjectInfo>(
                "get_project_info",
                new { },
                cancellationToken));

        cancellationToken.ThrowIfCancellationRequested();
        var sync = await Timed(
            "Staging device export",
            () => stager.StageAsync(device, plcName, cancellationToken));
        var selected = sync.Where(item =>
            string.Equals(item.PlcName, plcName, StringComparison.Ordinal)).ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException(
                $"rebuild_export did not return selected PLC '{plcName}'.");
        }

        var approvalRequired = selected.Any(result =>
            !result.BaselineExisted
            || result.Added.Length + result.Changed.Length + result.Removed.Length > 0);
        return new ReadProjectContextResult
        {
            ProjectName = string.IsNullOrWhiteSpace(info.Name) ? "unknown" : info.Name!,
            WorkbenchId = device.WorkbenchId,
            WorktreeId = device.WorktreeId,
            DeviceId = device.DeviceId,
            PlcName = plcName,
            ExportRoot = device.ExportedSourceRoot,
            ModifiedSourceRoot = device.ModifiedSourceRoot,
            StagingRoot = device.StagingRoot,
            DbPath = device.KnowledgeDbPath,
            Sync = selected,
            Ingest = null,
            ApprovalRequired = approvalRequired,
            UpToDate = !approvalRequired,
        };
    }

    private async Task<T> Timed<T>(string step, Func<Task<T>> action)
    {
        Log($"{step}…");
        var stopwatch = Stopwatch.StartNew();
        var result = await action().ConfigureAwait(false);
        Log($"{step} — done in {stopwatch.ElapsedMilliseconds / 1000.0:0.0}s");
        return result;
    }

    private void Log(string message) => progress?.Report(message);
}
