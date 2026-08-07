using Agent.Mcp;
using Contracts.Engineering;
using ModelContextProtocol;

namespace Agent.Workbench;

internal interface IStagingFileOperations
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    void CreateDirectory(string path);
    void MoveDirectory(string source, string destination);
    void DeleteDirectory(string path);
    IEnumerable<string> EnumerateEntries(string path);
    FileAttributes GetAttributes(string path);
    void DeleteFile(string path);
}

internal sealed class StagingFileOperations : IStagingFileOperations
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    public void DeleteDirectory(string path) => Directory.Delete(path);
    public IEnumerable<string> EnumerateEntries(string path) =>
        Directory.EnumerateFileSystemEntries(path);
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public void DeleteFile(string path) => File.Delete(path);
}

/// <summary>Stages one complete selected-device export without exposing a partial tree.</summary>
public sealed class SafeDeviceExportStager
{
    private readonly IMcpToolCaller engineering;
    private readonly DeviceOperationLock operationLock;
    private readonly IStagingFileOperations files;

    public SafeDeviceExportStager(
        IMcpToolCaller engineering,
        DeviceOperationLock? operationLock = null)
        : this(engineering, operationLock ?? new DeviceOperationLock(), new StagingFileOperations())
    {
    }

    internal SafeDeviceExportStager(
        IMcpToolCaller engineering,
        DeviceOperationLock operationLock,
        IStagingFileOperations files)
    {
        this.engineering = engineering ?? throw new ArgumentNullException(nameof(engineering));
        this.operationLock = operationLock ?? throw new ArgumentNullException(nameof(operationLock));
        this.files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public Task<SyncResult[]> StageAsync(
        DeviceContext device,
        string plcName,
        CancellationToken cancellationToken = default,
        IOperationProgress? progress = null,
        bool allowCompile = false) =>
        operationLock.RunAsync(
            device,
            token => StageCoreAsync(device, plcName, token, progress, allowCompile),
            cancellationToken);

    private async Task<SyncResult[]> StageCoreAsync(
        DeviceContext device,
        string plcName,
        CancellationToken cancellationToken,
        IOperationProgress? progress,
        bool allowCompile)
    {
        var attemptedCompile = false;
        return await StageAttemptAsync(device, plcName, cancellationToken, progress, allowCompile, attemptedCompile)
            .ConfigureAwait(false);
    }

    private async Task<SyncResult[]> StageAttemptAsync(
        DeviceContext device,
        string plcName,
        CancellationToken cancellationToken,
        IOperationProgress? progress,
        bool allowCompile,
        bool attemptedCompile)
    {
        progress?.Report(attemptedCompile
            ? "Preparing export staging area after compile..."
            : "Preparing export staging area...");
        var incoming = WorkbenchPaths.ResolveRelative(
            device.DeviceRoot,
            // Short name on purpose: TIA writes the full export tree under this directory and
            // fails with "Cannot create file" once a path exceeds the Windows 260-char limit.
            // The previous .staging-<guid>.incoming name alone consumed ~50 chars.
            $".st-{Guid.NewGuid().ToString("N")[..12]}");
        try
        {
            files.CreateDirectory(incoming);
            progress?.Report("Exporting PLC source...");
            var progressBridge = progress is null ? null : new McpProgressBridge(progress);
            var result = engineering is IProgressMcpToolCaller progressCaller
                ? await progressCaller.CallAsync<SyncResult[]>(
                    "rebuild_export",
                    new { outputDir = incoming, plcName },
                    progressBridge,
                    cancellationToken).ConfigureAwait(false)
                : await engineering.CallAsync<SyncResult[]>(
                    "rebuild_export",
                    new { outputDir = incoming, plcName },
                    cancellationToken).ConfigureAwait(false);
            progress?.Report("Writing export metadata...");
            foreach (var warning in result.SelectMany(item => item.HardwareWarnings).Distinct(StringComparer.Ordinal))
            {
                progress?.Report($"Hardware export warning (non-fatal): {warning}");
            }
            var selected = result.Where(item =>
                string.Equals(item.PlcName, plcName, StringComparison.Ordinal)).ToArray();
            if (selected.Length == 0 || selected.Any(item => item.Failed.Length > 0))
            {
                var failed = selected.Sum(item => item.Failed.Length);
                if (selected.Length > 0 && IsCompileRequired(selected) && !attemptedCompile)
                {
                    if (!allowCompile)
                    {
                        throw new WorkbenchLifecycleException(
                            "PLC_COMPILE_REQUIRED",
                            BuildCompileRequiredMessage(failed, selected));
                    }

                    progress?.Report("Compiling selected PLC before retrying export...");
                    var compile = await engineering.CallAsync<CompileResult>(
                            "compile_plc",
                            new { plcName },
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (string.Equals(compile.State, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new WorkbenchLifecycleException(
                            "PLC_COMPILE_FAILED",
                            BuildCompileFailedMessage(plcName, compile));
                    }

                    return await StageAttemptAsync(
                            device,
                            plcName,
                            cancellationToken,
                            progress,
                            allowCompile,
                            attemptedCompile: true)
                        .ConfigureAwait(false);
                }

                throw new WorkbenchLifecycleException(
                    "DEVICE_EXPORT_INCOMPLETE",
                    selected.Length == 0
                        ? $"The export did not return selected PLC '{plcName}'."
                        : BuildIncompleteExportMessage(failed, selected));
            }

            if (!files.FileExists(Path.Combine(incoming, "metadata.json")))
            {
                throw new WorkbenchLifecycleException(
                    "DEVICE_EXPORT_INCOMPLETE",
                    "The selected PLC export did not produce a component manifest; previous staging was preserved.");
            }

            ReplaceStaging(device, incoming);
            return selected;
        }
        finally
        {
            TryDeleteTree(incoming);
        }
    }

    private void ReplaceStaging(DeviceContext device, string incoming)
    {
        var backup = WorkbenchPaths.ResolveRelative(
            device.DeviceRoot,
            $".staging-{Guid.NewGuid():N}.backup");
        var previousMoved = false;
        var replacementInstalled = false;
        try
        {
            if (files.DirectoryExists(device.StagingRoot))
            {
                files.MoveDirectory(device.StagingRoot, backup);
                previousMoved = true;
            }

            files.MoveDirectory(incoming, device.StagingRoot);
            replacementInstalled = true;
        }
        catch (Exception primary)
        {
            if (previousMoved && !files.DirectoryExists(device.StagingRoot))
            {
                try
                {
                    files.MoveDirectory(backup, device.StagingRoot);
                    previousMoved = false;
                }
                catch (Exception restore)
                {
                    // The backup remains in place for manual recovery.
                    throw new AggregateException(
                        "Staging replacement failed and the previous staging tree could not be restored. The backup was preserved.",
                        primary,
                        restore);
                }
            }

            throw;
        }
        finally
        {
            // Delete backup only after a replacement was installed or restoration succeeded.
            // Cleanup is best effort and must never mask the staging result.
            if (replacementInstalled || !previousMoved)
            {
                TryDeleteTree(backup);
            }
        }
    }

    private void TryDeleteTree(string path)
    {
        try
        {
            DeleteTree(path);
        }
        catch (Exception)
        {
            // Cleanup is deliberately non-authoritative. The primary export/swap result and any
            // preserved backup are more important than removing a transient artifact.
        }
    }

    private void DeleteTree(string path)
    {
        if (!files.DirectoryExists(path))
        {
            return;
        }

        foreach (var entry in files.EnumerateEntries(path))
        {
            if (files.DirectoryExists(entry))
            {
                if ((files.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    files.DeleteDirectory(entry);
                }
                else
                {
                    DeleteTree(entry);
                }
            }
            else
            {
                files.DeleteFile(entry);
            }
        }

        files.DeleteDirectory(path);
    }

    private static string BuildIncompleteExportMessage(int failed, SyncResult[] selected)
    {
        var details = selected
            .SelectMany(result => result.Failed)
            .Select(DescribeFailure)
            .Where(detail => !string.IsNullOrWhiteSpace(detail))
            .Take(5)
            .ToArray();
        var message = $"The selected PLC export failed for {failed} component(s); previous staging was preserved.";
        if (details.Length == 0)
        {
            return message;
        }

        var omitted = failed > details.Length
            ? $" (+{failed - details.Length} more)"
            : string.Empty;
        return $"{message} Failed component(s): {string.Join("; ", details)}{omitted}.";
    }

    private static string DescribeFailure(SyncChange failure)
    {
        var identity = string.IsNullOrWhiteSpace(failure.Category)
            ? failure.Name
            : $"{failure.Category} {failure.Name}";
        if (string.IsNullOrWhiteSpace(identity))
        {
            identity = string.IsNullOrWhiteSpace(failure.SourcePath)
                ? "(unknown)"
                : failure.SourcePath;
        }

        return string.IsNullOrWhiteSpace(failure.Reason)
            ? identity
            : $"{identity} — {failure.Reason}";
    }

    private sealed class McpProgressBridge(IOperationProgress progress) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value)
        {
            if (!string.IsNullOrWhiteSpace(value.Message))
            {
                progress.Report(value.Message);
            }
        }
    }

    private static bool IsCompileRequired(SyncResult[] selected) =>
        selected.Any(result => result.Failed.Any(failure =>
            ContainsCompileSignal(failure.Reason)
            || ContainsCompileSignal(failure.Name)
            || ContainsCompileSignal(failure.SourcePath)));

    private static bool ContainsCompileSignal(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.Contains("compile", StringComparison.OrdinalIgnoreCase)
            || value.Contains("inconsistent", StringComparison.OrdinalIgnoreCase));

    private static string BuildCompileRequiredMessage(int failed, SyncResult[] selected) =>
        BuildIncompleteExportMessage(failed, selected)
        + " The PLC appears to need a compile before TIA can export the selected source. Allow automatic compile, or compile manually in TIA Portal and retry.";

    private static string BuildCompileFailedMessage(string plcName, CompileResult compile)
    {
        var details = compile.Messages
            .Where(message => string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
            .Select(message =>
            {
                var location = string.IsNullOrWhiteSpace(message.BlockName)
                    ? plcName
                    : message.NetworkNumber is null
                        ? message.BlockName
                        : $"{message.BlockName} network {message.NetworkNumber}";
                return $"{location}: {message.Text}";
            })
            .Where(detail => !string.IsNullOrWhiteSpace(detail))
            .Take(5)
            .ToArray();
        var suffix = details.Length == 0
            ? string.Empty
            : $" Error(s): {string.Join("; ", details)}.";
        return $"Automatic PLC compile failed for '{plcName}'. Previous staging was preserved.{suffix}";
    }
}
