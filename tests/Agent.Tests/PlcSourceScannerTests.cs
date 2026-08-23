using Agent.Mcp;
using Agent.Workbench;
using Contracts.Engineering;
using Xunit;

namespace Agent.Tests;

public sealed class PlcSourceScannerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "plc-source-scanner-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScanExportsThenReturnsStableNormalizedObjects()
    {
        var engineering = new ScannerEngineeringCaller(root, "same");
        var scanner = new PlcSourceScanner(engineering);

        var result = await scanner.ScanAsync(Context(), CancellationToken.None);

        Assert.Equal("same", result.ProjectChecksum);
        Assert.Single(result.Objects);
        Assert.Equal(new[] { "get_plc_checksums", "sync_export", "get_plc_checksums" }, engineering.Calls);
    }

    [Fact]
    public async Task ScanUsesRebuildExportWhenForceFullExportIsTrue()
    {
        var engineering = new ScannerEngineeringCaller(root, "same");
        var scanner = new PlcSourceScanner(engineering);

        var result = await scanner.ScanAsync(Context(), CancellationToken.None, forceFullExport: true);

        Assert.Equal("same", result.ProjectChecksum);
        Assert.Single(result.Objects);
        Assert.Equal(new[] { "get_plc_checksums", "rebuild_export", "get_plc_checksums" }, engineering.Calls);
    }

    [Fact]
    public async Task ScanRejectsChecksumMovementDuringExport()
    {
        var engineering = new ScannerEngineeringCaller(root, "before", "after");
        var scanner = new PlcSourceScanner(engineering);

        var error = await Assert.ThrowsAsync<ReconciliationException>(() =>
            scanner.ScanAsync(Context(), CancellationToken.None));

        Assert.Equal("TIA_CHANGED_DURING_SCAN", error.Code);
    }

    [Fact]
    public async Task ScanCanCompileWhenTheUserExplicitlyAllowsIt()
    {
        var engineering = new ScannerEngineeringCaller(root, string.Empty, "compiled");
        var scanner = new PlcSourceScanner(engineering);

        var result = await scanner.ScanAsync(Context(), CancellationToken.None, allowCompile: true);

        Assert.Equal("compiled", result.ProjectChecksum);
        Assert.Contains("compile_plc", engineering.Calls);
    }

    [Fact]
    public async Task ScanReportsObjectsThatOpennessCannotExport()
    {
        var engineering = new ScannerEngineeringCaller(root, "same") { AddUnsupported = true };
        var scanner = new PlcSourceScanner(engineering);

        var result = await scanner.ScanAsync(Context(), CancellationToken.None);

        var unsupported = Assert.Single(result.UnsupportedObjects);
        Assert.Equal("F_Main", unsupported.Name);
        Assert.Equal("TIA_EXPORT_UNSUPPORTED", unsupported.Reason);
    }

    private DeviceContext Context()
    {
        var deviceRoot = Path.Combine(root, "devices", "PLC_1");
        Directory.CreateDirectory(deviceRoot);
        return new DeviceContext("wb", "master", "device-1", root, Path.Combine(root, "worktree"), deviceRoot,
            Path.Combine(deviceRoot, "source"), Path.Combine(deviceRoot, "staging"), Path.Combine(deviceRoot, "plc-knowledge.db"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class ScannerEngineeringCaller : IMcpToolCaller
    {
        private readonly string root;
        private readonly string[] checksums;
        private int checksumIndex;

        public ScannerEngineeringCaller(string root, params string[] checksums)
        {
            this.root = root;
            this.checksums = checksums;
        }

        public List<string> Calls { get; } = new();
        public bool AddUnsupported { get; set; }

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (tool == "get_plc_checksums")
            {
                var checksum = checksums[Math.Min(checksumIndex++, checksums.Length - 1)];
                return Task.FromResult((T)(object)new[]
                {
                    new PlcChecksumInfo
                    {
                        PlcName = "PLC_1",
                        ProjectIdentity = "project-1",
                        SoftwareChecksum = checksum,
                    },
                });
            }

            if (tool == "sync_export" || tool == "rebuild_export")
            {
                var outputDir = (string)args.GetType().GetProperty("outputDir")!.GetValue(args)!;
                Directory.CreateDirectory(Path.Combine(outputDir, "Blocks"));
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "Main.xml"), "<Document><SW.Blocks.OB ID=\"1\" /></Document>");
                File.WriteAllText(Path.Combine(outputDir, "metadata.json"), "{}");
                return Task.FromResult((T)(object)new[]
                {
                    new SyncResult
                    {
                        PlcName = "PLC_1",
                        ExportRoot = outputDir,
                        Status = "updated",
                        Unsupported = AddUnsupported
                            ? new[] { new UnsupportedSourceObject { Name = "F_Main", Reason = "TIA_EXPORT_UNSUPPORTED" } }
                            : Array.Empty<UnsupportedSourceObject>(),
                    },
                });
            }

            if (tool == "compile_plc")
                return Task.FromResult((T)(object)new CompileResult { State = "success" });

            throw new InvalidOperationException(tool);
        }
    }
}
