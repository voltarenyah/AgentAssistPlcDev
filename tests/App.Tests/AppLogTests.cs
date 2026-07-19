using System;
using System.IO;
using App.Logging;
using Xunit;

namespace App.Tests;

public sealed class AppLogTests
{
    [Fact]
    public void WritesLinesToInitializedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "App.Tests", Guid.NewGuid().ToString("N"), "app.log");

        AppLog.Initialize(path);
        AppLog.Write("first");
        AppLog.Write("second");

        Assert.Equal(path, AppLog.FilePath);
        var lines = File.ReadAllLines(path);
        Assert.Equal(new[] { "first", "second" }, lines);
    }
}
