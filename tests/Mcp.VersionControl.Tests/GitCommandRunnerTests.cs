using System.Diagnostics;
using Mcp.VersionControl.Git;
using Xunit;

namespace Mcp.VersionControl.Tests;

public sealed class GitCommandRunnerTests
{
    [Fact]
    public void RunTerminatesHungProcessAndReportsTimeout()
    {
        var runner = new GitCommandRunner(TimeSpan.FromMilliseconds(250));
        var (executable, arguments) = SlowCommand();
        var stopwatch = Stopwatch.StartNew();

        var error = Assert.Throws<VcInternalException>(() =>
            runner.Run(executable, "TEST_FAILED", "The test command failed.", arguments));

        stopwatch.Stop();
        Assert.Equal("GIT_TIMEOUT", error.Code);
        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RunProvidesClosedStandardInputToChildProcess()
    {
        var helper = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "Mcp.VersionControl.TestHost",
            "bin", "Debug", "net8.0",
            "Mcp.VersionControl.TestHost.dll"));
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(helper);

        using var process = Process.Start(startInfo)!;
        var exited = process.WaitForExit(5_000);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();

        Assert.True(exited, "The hosted runner did not finish.");
        Assert.Equal(0, process.ExitCode);
        Assert.Contains("stdin-closed", output, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(error), error);
    }

    private static (string Executable, string[] Arguments) SlowCommand() =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/d", "/s", "/c", "ping 127.0.0.1 -n 30 > nul" })
            : ("/bin/sh", new[] { "-c", "sleep 30" });

}
