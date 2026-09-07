using Agent.Mcp;
using Contracts.Engineering;
using System.Diagnostics;

namespace Agent.Workbench;

public sealed record DeviceScanResult(
    string DeviceId,
    string PlcName,
    string ProjectIdentity,
    string ProjectChecksum,
    IReadOnlyList<SourceObjectSnapshot> Objects,
    IReadOnlyList<UnsupportedSourceObject> UnsupportedObjects,
    string CompletedAt,
    IReadOnlyList<SyncResult>? SyncResults = null,
    IReadOnlyList<ComparisonTiming>? Timings = null);

/// <summary>Produces a complete, checksum-stable source snapshot for one PLC device.</summary>
public sealed class PlcSourceScanner
{
    private readonly IMcpToolCaller engineering;
    private readonly SafeDeviceExportStager stager;
    private readonly SemaphoreSlim engineeringSession;

    public PlcSourceScanner(
        IMcpToolCaller engineering,
        DeviceOperationLock? operationLock = null,
        SemaphoreSlim? engineeringSession = null)
    {
        this.engineering = engineering ?? throw new ArgumentNullException(nameof(engineering));
        stager = new SafeDeviceExportStager(engineering, operationLock);
        this.engineeringSession = engineeringSession ?? new SemaphoreSlim(1, 1);
    }

    public async Task<DeviceScanResult> ScanAsync(
        DeviceContext device,
        CancellationToken cancellationToken = default,
        IOperationProgress? progress = null,
        string? plcName = null,
        bool allowCompile = false,
        bool forceFullExport = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        plcName ??= new DirectoryInfo(device.DeviceRoot).Name;
        await engineeringSession.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var timings = new List<ComparisonTiming>();
            var before = await MeasureAsync(
                    timings,
                    "plc-checksum-before-export",
                    "Read the compiled software checksum before source export, so the staged snapshot can be proven stable.",
                    plcName,
                    () => ReadChecksumAsync(plcName, cancellationToken),
                    checksum => string.IsNullOrWhiteSpace(checksum.SoftwareChecksum) ? "Checksum unavailable." : "Checksum available.")
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(before.SoftwareChecksum) && allowCompile)
            {
                progress?.Report($"Compiling PLC '{plcName}' before export...");
                var compile = await MeasureAsync(
                        timings,
                        "plc-compile",
                        "Compile the selected PLC because TIA did not provide a software checksum.",
                        plcName,
                        () => engineering.CallAsync<CompileResult>("compile_plc", new { plcName }, cancellationToken),
                        result => $"Compile state: {result.State}.")
                    .ConfigureAwait(false);
                if (string.Equals(compile.State, "error", StringComparison.OrdinalIgnoreCase))
                    throw new WorkbenchLifecycleException("PLC_COMPILE_FAILED", $"Automatic PLC compile failed for '{plcName}'.");
                before = await MeasureAsync(
                        timings,
                        "plc-checksum-after-compile",
                        "Re-read the compiled software checksum after automatic compilation.",
                        plcName,
                        () => ReadChecksumAsync(plcName, cancellationToken),
                        checksum => string.IsNullOrWhiteSpace(checksum.SoftwareChecksum) ? "Checksum still unavailable." : "Checksum available.")
                    .ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(before.SoftwareChecksum))
                throw new ReconciliationException("PLC_CHECKSUM_UNAVAILABLE", $"TIA did not provide a compiled software checksum for PLC '{plcName}'.");

            var staged = await MeasureAsync(
                    timings,
                    forceFullExport ? "plc-rebuild-export" : "plc-sync-export",
                    forceFullExport
                        ? "Fully export selected PLC XML to staging for an authoritative source scan."
                        : "Incrementally export only TIA source candidates into staging for an authoritative source scan.",
                    plcName,
                    () => stager.StageAsync(device, plcName, cancellationToken, progress, allowCompile, forceFullExport),
                    DescribeExportOutcome)
                .ConfigureAwait(false);
            var after = await MeasureAsync(
                    timings,
                    "plc-checksum-after-export",
                    "Re-read the software checksum to reject an export made while TIA changed.",
                    plcName,
                    () => ReadChecksumAsync(plcName, cancellationToken),
                    checksum => string.IsNullOrWhiteSpace(checksum.SoftwareChecksum) ? "Checksum unavailable." : "Checksum available.")
                .ConfigureAwait(false);
            if (!string.Equals(before.SoftwareChecksum, after.SoftwareChecksum, StringComparison.Ordinal))
            {
                throw new ReconciliationException(
                    "TIA_CHANGED_DURING_SCAN",
                    $"PLC '{plcName}' changed while its source was being exported; the staged snapshot is not authoritative.");
            }

            var objects = Measure(
                timings,
                "staged-source-index",
                "Read and normalize every staged XML file for the later master-versus-TIA comparison.",
                plcName,
                () => new SourceTreeReader().Read(device.StagingRoot),
                items => $"Indexed {items.Count} staged XML object(s).");
            var unsupported = staged
                .SelectMany(result => result.Unsupported)
                .ToArray();
            return new DeviceScanResult(
                device.DeviceId,
                plcName,
                before.ProjectIdentity,
                before.SoftwareChecksum!,
                objects,
                unsupported,
                DateTimeOffset.UtcNow.ToString("O"),
                staged,
                timings);
        }
        finally
        {
            engineeringSession.Release();
        }
    }

    private async Task<PlcChecksumInfo> ReadChecksumAsync(
        string plcName,
        CancellationToken cancellationToken)
    {
        var checksums = await engineering.CallAsync<PlcChecksumInfo[]>(
                "get_plc_checksums",
                new { plcName },
                cancellationToken)
            .ConfigureAwait(false);
        return checksums.FirstOrDefault(info =>
                   string.Equals(info.PlcName, plcName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ReconciliationException(
                "PLC_NOT_FOUND",
                $"TIA did not return checksum information for PLC '{plcName}'.");
    }

    private static async Task<T> MeasureAsync<T>(
        ICollection<ComparisonTiming> timings,
        string phase,
        string purpose,
        string plcName,
        Func<Task<T>> action,
        Func<T, string> describe)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await action().ConfigureAwait(false);
        stopwatch.Stop();
        timings.Add(new ComparisonTiming(phase, purpose, plcName, stopwatch.ElapsedMilliseconds, describe(result)));
        return result;
    }

    private static T Measure<T>(
        ICollection<ComparisonTiming> timings,
        string phase,
        string purpose,
        string plcName,
        Func<T> action,
        Func<T, string> describe)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = action();
        stopwatch.Stop();
        timings.Add(new ComparisonTiming(phase, purpose, plcName, stopwatch.ElapsedMilliseconds, describe(result)));
        return result;
    }

    private static string DescribeExportOutcome(SyncResult[] results)
    {
        var selected = results.ToArray();
        return $"{selected.Length} PLC result(s): "
            + $"added {selected.Sum(result => result.Added.Length)}, "
            + $"changed {selected.Sum(result => result.Changed.Length)}, "
            + $"touched {selected.Sum(result => result.Touched.Length)}, "
            + $"removed {selected.Sum(result => result.Removed.Length)}, "
            + $"failed {selected.Sum(result => result.Failed.Length)}, "
            + $"unsupported {selected.Sum(result => result.Unsupported.Length)}.";
    }
}
