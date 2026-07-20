using System;
using System.IO;
using System.Text.Json;
using Contracts.Sandbox;
using Xunit;

namespace Contracts.Tests;

public sealed class SandboxConfigTests : IDisposable
{
    private readonly string directory;

    public SandboxConfigTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "sandbox-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string WriteConfig(string json)
    {
        var path = Path.Combine(directory, "sandbox.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void MissingFileYieldsBuiltInDefaults()
    {
        var config = SandboxConfig.Load(Path.Combine(directory, "does-not-exist.json"));

        Assert.Equal(SandboxConfig.DefaultMaxDestructiveCallsPerSession, config.MaxDestructiveCallsPerSession);
        Assert.Equal(SandboxTier.Destructive, config.Policy.Classify("save_project"));
        Assert.Contains("defaults", config.SourceDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SandboxConfig.DefaultRoots.Count, config.PathJail.Roots.Count);
    }

    [Fact]
    public void OverridesExtendDefaults()
    {
        var extraRoot = Path.Combine(directory, "exports");
        var config = SandboxConfig.Load(WriteConfig($$"""
        {
          "tiers": { "compile_plc": "deny", "my_tool": "read" },
          "allowedRoots": ["{{extraRoot.Replace("\\", "\\\\")}}"],
          "maxDestructiveCallsPerSession": 3
        }
        """));

        Assert.Equal(SandboxTier.Denied, config.Policy.Classify("compile_plc"));
        Assert.Equal(SandboxTier.Read, config.Policy.Classify("my_tool"));
        Assert.Equal(SandboxTier.Read, config.Policy.Classify("list_blocks")); // defaults intact
        Assert.Equal(3, config.MaxDestructiveCallsPerSession);
        Assert.Equal(SandboxConfig.DefaultRoots.Count + 1, config.PathJail.Roots.Count);
        Assert.Null(Record.Exception(() => config.PathJail.Validate(Path.Combine(extraRoot, "a.xml"), "outputDir")));
    }

    [Fact]
    public void InvalidJsonYieldsBuiltInDefaults()
    {
        var config = SandboxConfig.Load(WriteConfig("{ not json"));

        Assert.Equal(SandboxConfig.DefaultMaxDestructiveCallsPerSession, config.MaxDestructiveCallsPerSession);
        Assert.Equal(SandboxTier.Destructive, config.Policy.Classify("import_block"));
    }

    [Fact]
    public void UnrecognizedTierValueFailsClosed()
    {
        var config = SandboxConfig.Load(WriteConfig("""{ "tiers": { "compile_plc": "yolo" } }"""));
        Assert.Equal(SandboxTier.Denied, config.Policy.Classify("compile_plc"));
    }

    [Fact]
    public void NegativeBudgetClampsToZero()
    {
        var config = SandboxConfig.Load(WriteConfig("""{ "maxDestructiveCallsPerSession": -5 }"""));
        Assert.Equal(0, config.MaxDestructiveCallsPerSession);
    }
}

public sealed class SandboxAuditTests : IDisposable
{
    private readonly string directory;

    public SandboxAuditTests()
    {
        directory = Path.Combine(Path.GetTempPath(), "sandbox-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RecordAppendsOneJsonLinePerDecision()
    {
        var audit = new SandboxAudit(directory, "agent");
        audit.Record("save_project", "destructive", "deny", "SANDBOX_USER_DENIED: no");
        audit.Record("list_blocks", "read", "allow");

        Assert.Null(audit.LastError);
        var lines = File.ReadAllLines(Path.Combine(directory, "agent.jsonl"));
        Assert.Equal(2, lines.Length);

        using var document = JsonDocument.Parse(lines[0]);
        var entry = document.RootElement;
        Assert.Equal("agent", entry.GetProperty("src").GetString());
        Assert.Equal("save_project", entry.GetProperty("tool").GetString());
        Assert.Equal("destructive", entry.GetProperty("tier").GetString());
        Assert.Equal("deny", entry.GetProperty("decision").GetString());
        Assert.Contains("SANDBOX_USER_DENIED", entry.GetProperty("detail").GetString());
        Assert.True(entry.GetProperty("ts").GetDateTimeOffset() > DateTimeOffset.Now.AddMinutes(-5));
    }

    [Fact]
    public void WriteFailureIsCapturedNotThrown()
    {
        // A file where the directory should be forces the append to fail.
        var blocker = Path.Combine(directory, "blocked");
        File.WriteAllText(blocker, "occupied");
        var audit = new SandboxAudit(blocker, "agent");

        Record.Exception(() => audit.Record("x", "read", "allow"));
        Assert.NotNull(audit.LastError);
    }
}
