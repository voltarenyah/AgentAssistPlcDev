using System;
using System.IO;
using Xunit;

namespace App.Tests;

public sealed class AppSettingsTests
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    [Fact]
    public void ConfigOverridesWin()
    {
        using var directory = new TempDirectory();
        var config = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(config, """{ "apiKey": "secret", "engineeringServerPath": "C:\\custom\\eng.exe", "knowledgeServerPath": "C:\\custom\\kn.exe" }""");

        var settings = AppSettings.Load(config, Path.Combine(directory.Path, "no-sln-here"));

        Assert.Equal(@"C:\custom\eng.exe", settings.EngineeringServerPath);
        Assert.Equal(@"C:\custom\kn.exe", settings.KnowledgeServerPath);
    }

    [Fact]
    public void DefaultsFollowSolutionLayout()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "AgentAssistPlcDev.sln"), string.Empty);
        var baseDirectory = Path.Combine(directory.Path, "src", "App", "bin", "Debug", "net8.0-windows");
        Directory.CreateDirectory(baseDirectory);

        var settings = AppSettings.Load(Path.Combine(directory.Path, "missing-config.json"), baseDirectory);

        Assert.Equal(
            Path.Combine(directory.Path, "src", "Mcp.Engineering", "bin", BuildConfiguration, "net48", "Mcp.Engineering.exe"),
            settings.EngineeringServerPath);
        Assert.Equal(
            Path.Combine(directory.Path, "src", "Mcp.Knowledge", "bin", BuildConfiguration, "net8.0", "Mcp.Knowledge.exe"),
            settings.KnowledgeServerPath);
    }

    [Fact]
    public void MissingSolutionAndConfigThrows()
    {
        using var directory = new TempDirectory();

        var error = Assert.Throws<InvalidOperationException>(() =>
            AppSettings.Load(Path.Combine(directory.Path, "missing-config.json"), directory.Path));

        Assert.Contains("AgentAssistPlcDev.sln", error.Message);
    }

    [Fact]
    public void ExportRootSanitizesInvalidFileNameChars()
    {
        var root = AppSettings.ResolveExportRoot("PEI_SinoARP_Master:V4");

        Assert.EndsWith(Path.Combine("PlcAiAssistant", "exports", "PEI_SinoARP_Master_V4"), root);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "App.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
