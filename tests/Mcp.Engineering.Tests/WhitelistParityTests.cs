using System.Diagnostics;
using System;
using System.IO;
using System.Linq;
using AutomationWorkbench.OpennessWhitelist;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class WhitelistParityTests
{
    [Fact]
    public void HelperAndPowerShellReferenceGenerateIdenticalRegistryContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "automation-workbench-whitelist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "Mcp.Engineering.exe");
        File.WriteAllBytes(executable, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        File.SetLastWriteTimeUtc(executable, new DateTime(2026, 8, 1, 12, 34, 56, 789, DateTimeKind.Utc));

        try
        {
            var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "register-whitelist.ps1"));
            Assert.True(File.Exists(script), script);
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -TargetPath \"{executable}\"",
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(process);
            process!.WaitForExit();
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.Equal(0, process.ExitCode);
            Assert.Contains(".reg file generated", output);

            var powershellContent = File.ReadAllText(Path.Combine(root, "register-whitelist.reg"));
            var helperContent = WhitelistDocument.FromExecutable(executable).RenderRegFile();
            Assert.Equal(
                NormalizeNewlines(powershellContent),
                NormalizeNewlines(helperContent));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n");
}
