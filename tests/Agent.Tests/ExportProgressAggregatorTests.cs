using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class ExportProgressAggregatorTests
{
    private sealed class RecordingProgress : IOperationProgress
    {
        public List<string> Messages { get; } = [];
        public void Report(string message) => Messages.Add(message);
    }

    [Fact]
    public void CounterMessagesBecomeCumulativeAcrossDevices()
    {
        var recording = new RecordingProgress();
        var aggregator = new ExportProgressAggregator(recording, [100, 200]);

        aggregator.Report("Exported PLC source files: 40");
        aggregator.DeviceCompleted();
        aggregator.Report("Exported PLC source files: 5");

        Assert.Equal(
            new[] { "Exported PLC source files: 40 of 300", "Exported PLC source files: 105 of 300" },
            recording.Messages);
    }

    [Fact]
    public void NonCounterMessagesPassThroughUnchanged()
    {
        var recording = new RecordingProgress();
        var aggregator = new ExportProgressAggregator(recording, [10]);

        aggregator.Report("Exporting block Main_OB1...");
        aggregator.Report("Preparing export staging area...");

        Assert.Equal(
            new[] { "Exporting block Main_OB1...", "Preparing export staging area..." },
            recording.Messages);
    }

    [Fact]
    public void CounterMessagesPassThroughWhenNoManifestTotalsExist()
    {
        var recording = new RecordingProgress();
        var aggregator = new ExportProgressAggregator(recording, [0, 0]);

        Assert.False(aggregator.HasTotals);
        aggregator.Report("Exported PLC source files: 7");

        Assert.Equal(new[] { "Exported PLC source files: 7" }, recording.Messages);
    }

    [Fact]
    public void DeviceCompletedKeepsActualCountWhenItExceedsTheManifestExpectation()
    {
        var recording = new RecordingProgress();
        var aggregator = new ExportProgressAggregator(recording, [10, 10]);

        aggregator.Report("Exported PLC source files: 14");
        aggregator.DeviceCompleted();
        aggregator.Report("Exported PLC source files: 1");

        Assert.Equal("Exported PLC source files: 15 of 20", recording.Messages[1]);
    }
}
