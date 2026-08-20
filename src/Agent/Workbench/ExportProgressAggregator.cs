namespace Agent.Workbench;

/// <summary>
/// Rewrites the per-device export counter messages ("Exported PLC source files: N") emitted
/// during a multi-device compare into cumulative "current of total" messages, so callers can
/// show a percentage. The expected per-device totals come from the previous export manifest
/// (the metadata next to the staged XML); when no manifest exists yet the messages pass
/// through unchanged.
/// </summary>
public sealed class ExportProgressAggregator : IOperationProgress
{
    internal const string CounterMessagePrefix = "Exported PLC source files: ";

    private readonly IOperationProgress inner;
    private readonly IReadOnlyList<int> expectedPerDevice;
    private readonly int grandTotal;
    private int deviceIndex;
    private int completedFiles;
    private int currentDeviceFiles;

    public ExportProgressAggregator(IOperationProgress inner, IReadOnlyList<int> expectedPerDevice)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.expectedPerDevice = expectedPerDevice ?? throw new ArgumentNullException(nameof(expectedPerDevice));
        grandTotal = expectedPerDevice.Sum();
    }

    /// <summary>False when no previous manifest reported any exported file; messages then pass through.</summary>
    public bool HasTotals => grandTotal > 0;

    /// <summary>Advances to the next device: the files reported (or expected) so far become the baseline.</summary>
    public void DeviceCompleted()
    {
        completedFiles += Math.Max(ExpectedFor(deviceIndex), currentDeviceFiles);
        currentDeviceFiles = 0;
        deviceIndex++;
    }

    public void Report(string message)
    {
        if (HasTotals
            && !string.IsNullOrWhiteSpace(message)
            && message.StartsWith(CounterMessagePrefix, StringComparison.Ordinal)
            && int.TryParse(message[CounterMessagePrefix.Length..], out int deviceFiles))
        {
            currentDeviceFiles = deviceFiles;
            inner.Report($"{CounterMessagePrefix}{completedFiles + deviceFiles} of {grandTotal}");
            return;
        }

        inner.Report(message);
    }

    private int ExpectedFor(int index) =>
        index >= 0 && index < expectedPerDevice.Count ? expectedPerDevice[index] : 0;
}
