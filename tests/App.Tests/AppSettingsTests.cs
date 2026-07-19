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
    public void DeepSeekKeyRoundTripsAndPreservesExistingKeys()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "AgentAssistPlcDev.sln"), string.Empty);
        var config = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(config, """{ "engineeringServerPath": "C:\\custom\\eng.exe", "knowledgeServerPath": "C:\\custom\\kn.exe" }""");

        AppSettings.SaveDeepSeekApiKey(config, "sk-test-123");

        var settings = AppSettings.Load(config, Path.Combine(directory.Path, "no-sln-here"));
        Assert.Equal("sk-test-123", settings.DeepSeekApiKey);
        Assert.True(settings.HasDeepSeekApiKey);
        // Merge write kept the pre-existing overrides.
        Assert.Equal(@"C:\custom\eng.exe", settings.EngineeringServerPath);
        Assert.Equal(@"C:\custom\kn.exe", settings.KnowledgeServerPath);
    }

    [Fact]
    public void DeepSeekDefaultsWhenKeyAbsent()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "AgentAssistPlcDev.sln"), string.Empty);
        var baseDirectory = Path.Combine(directory.Path, "src", "App", "bin", "Debug", "net8.0-windows");
        Directory.CreateDirectory(baseDirectory);

        var settings = AppSettings.Load(Path.Combine(directory.Path, "missing-config.json"), baseDirectory);

        Assert.Null(settings.DeepSeekApiKey);
        Assert.False(settings.HasDeepSeekApiKey);
        Assert.Equal("deepseek-v4-flash", settings.DeepSeekModel);
        Assert.Equal("https://api.deepseek.com", settings.DeepSeekBaseUrl);
        Assert.True(settings.DeepSeekThinkingEnabled);
        Assert.Equal("high", settings.DeepSeekReasoningEffort);
        Assert.Equal(1.0, settings.DeepSeekTemperature);
        Assert.Equal(1.0, settings.DeepSeekTopP);
    }

    [Fact]
    public void DeepSeekChatSettingsRoundTrip()
    {
        using var directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "AgentAssistPlcDev.sln"), string.Empty);
        var config = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(config, """{ "deepSeekApiKey": "sk-keep" }""");

        AppSettings.SaveDeepSeekChatSettings(config, "deepseek-v4-pro", false, "max", 0.3, 0.9);

        var settings = AppSettings.Load(config, Path.Combine(directory.Path, "no-sln-here"));
        Assert.Equal("deepseek-v4-pro", settings.DeepSeekModel);
        Assert.False(settings.DeepSeekThinkingEnabled);
        Assert.Equal("max", settings.DeepSeekReasoningEffort);
        Assert.Equal(0.3, settings.DeepSeekTemperature);
        Assert.Equal(0.9, settings.DeepSeekTopP);
        // Merge write kept the pre-existing key.
        Assert.Equal("sk-keep", settings.DeepSeekApiKey);
    }

    [Fact]
    public void SaveDeepSeekApiKeyRejectsEmpty()
    {
        using var directory = new TempDirectory();

        Assert.Throws<ArgumentException>(() =>
            AppSettings.SaveDeepSeekApiKey(Path.Combine(directory.Path, "config.json"), "  "));
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
