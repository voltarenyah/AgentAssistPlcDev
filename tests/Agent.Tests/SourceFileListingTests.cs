using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class SourceFileListingTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(
        Path.GetTempPath(),
        "source-file-listing-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void ListReturnsSortedRelativeXmlPathsOnly()
    {
        var device = CreateDeviceContext();
        Write(device.ExportedSourceRoot, "Blocks/Main [OB1].xml");
        Write(device.ExportedSourceRoot, "Blocks/FC_LAD_SimulateCylinder_Call [FC1].xml");
        Write(device.ExportedSourceRoot, "DB/Data [DB1].xml");
        File.WriteAllText(Path.Combine(device.ExportedSourceRoot, "metadata.json"), "{}");

        Assert.Equal(
            new[]
            {
                "Blocks/FC_LAD_SimulateCylinder_Call [FC1].xml",
                "Blocks/Main [OB1].xml",
                "DB/Data [DB1].xml",
            },
            SourceFileListing.List(device));
    }

    [Fact]
    public void FormatShowsEmptyStateWhenExportIsMissing()
    {
        var device = CreateDeviceContext();

        Assert.Equal(
            "Source files: (none — refresh the device export first)",
            SourceFileListing.Format(device));
    }

    [Fact]
    public void FormatMarksFilesThatHaveAnOverlay()
    {
        var device = CreateDeviceContext();
        Write(device.ExportedSourceRoot, "Blocks/A.xml");
        Write(device.ExportedSourceRoot, "Blocks/B.xml");
        Write(device.ModifiedSourceRoot, "Blocks/B.xml");

        var formatted = SourceFileListing.Format(device);
        var lines = formatted.Split(Environment.NewLine);

        Assert.Contains("- Blocks/A.xml", lines);
        Assert.Contains("- Blocks/B.xml (overlay)", lines);
    }

    [Fact]
    public void FormatCapsLongListingsWithOverflowSummary()
    {
        var device = CreateDeviceContext();
        for (var i = 0; i < SourceFileListing.MaxListed + 3; i++)
        {
            Write(device.ExportedSourceRoot, $"Blocks/B{i:D4}.xml");
        }

        var formatted = SourceFileListing.Format(device);

        Assert.Contains($"- Blocks/B{SourceFileListing.MaxListed - 1:D4}.xml", formatted);
        Assert.DoesNotContain($"B{SourceFileListing.MaxListed:D4}.xml", formatted);
        Assert.Contains("… and 3 more", formatted);
    }

    private void Write(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<x/>");
    }

    private DeviceContext CreateDeviceContext()
    {
        var workbenchRoot = Path.Combine(tempRoot, "workbench");
        var worktreeRoot = Path.Combine(workbenchRoot, "worktrees", "wt");
        var deviceRoot = Path.Combine(worktreeRoot, "devices", "PLC_1");
        return new DeviceContext(
            "wb-1",
            "wt-1",
            "dev-1",
            workbenchRoot,
            worktreeRoot,
            deviceRoot,
            Path.Combine(deviceRoot, "exported-source"),
            Path.Combine(deviceRoot, "modified-source"),
            Path.Combine(deviceRoot, "staging"),
            Path.Combine(deviceRoot, "plc-knowledge.db"));
    }
}
