using Agent.Mcp;
using Contracts.Engineering;

namespace Agent.Workbench;

public sealed record DeviceScanResult(
    string DeviceId,
    string PlcName,
    string ProjectIdentity,
    string ProjectChecksum,
    IReadOnlyList<SourceObjectSnapshot> Objects,
    IReadOnlyList<UnsupportedSourceObject> UnsupportedObjects,
    string CompletedAt);

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
        bool allowCompile = false)
    {
        ArgumentNullException.ThrowIfNull(device);
        plcName ??= new DirectoryInfo(device.DeviceRoot).Name;
        await engineeringSession.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await ReadChecksumAsync(plcName, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(before.SoftwareChecksum) && allowCompile)
            {
                progress?.Report($"Compiling PLC '{plcName}' before export...");
                var compile = await engineering.CallAsync<CompileResult>(
                        "compile_plc",
                        new { plcName },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (string.Equals(compile.State, "error", StringComparison.OrdinalIgnoreCase))
                    throw new WorkbenchLifecycleException("PLC_COMPILE_FAILED", $"Automatic PLC compile failed for '{plcName}'.");
                before = await ReadChecksumAsync(plcName, cancellationToken).ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(before.SoftwareChecksum))
                throw new ReconciliationException("PLC_CHECKSUM_UNAVAILABLE", $"TIA did not provide a compiled software checksum for PLC '{plcName}'.");

            var staged = await stager.StageAsync(
                    device,
                    plcName,
                    cancellationToken,
                    progress)
                .ConfigureAwait(false);
            var after = await ReadChecksumAsync(plcName, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(before.SoftwareChecksum, after.SoftwareChecksum, StringComparison.Ordinal))
            {
                throw new ReconciliationException(
                    "TIA_CHANGED_DURING_SCAN",
                    $"PLC '{plcName}' changed while its source was being exported; the staged snapshot is not authoritative.");
            }

            var objects = new SourceTreeReader().Read(device.StagingRoot);
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
                DateTimeOffset.UtcNow.ToString("O"));
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
}
