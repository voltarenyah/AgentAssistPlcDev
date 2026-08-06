using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class LegacyExportCleanupTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"legacy-export-cleanup-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("export")]
    [InlineData("Exports")]
    public void RecognizedLegacyExportCacheIsRemoved(string directoryName)
    {
        var candidate = WriteCandidate(directoryName, """{"schemaVersion":"1.0","exportRoot":"x","components":[]}""");

        var notes = LegacyExportCleanup.RemoveLegacyExportCaches(root);

        Assert.False(Directory.Exists(candidate));
        Assert.Contains(notes, note => note.Contains("Removed", StringComparison.Ordinal));
    }

    [Fact]
    public void ManifestWithComponentsOnlyIsRecognized()
    {
        var candidate = WriteCandidate("Exports", """{"schemaVersion":"1.0","components":[]}""");

        LegacyExportCleanup.RemoveLegacyExportCaches(root);

        Assert.False(Directory.Exists(candidate));
    }

    [Fact]
    public void DirectoryWithoutManifestIsKept()
    {
        var candidate = Path.Combine(root, "Exports");
        Directory.CreateDirectory(candidate);
        File.WriteAllText(Path.Combine(candidate, "notes.txt"), "user content");

        var notes = LegacyExportCleanup.RemoveLegacyExportCaches(root);

        Assert.True(Directory.Exists(candidate));
        Assert.Equal("user content", File.ReadAllText(Path.Combine(candidate, "notes.txt")));
        Assert.Contains(notes, note => note.Contains("Kept", StringComparison.Ordinal));
    }

    [Fact]
    public void ManifestWithoutOurMarkersIsKept()
    {
        var candidate = WriteCandidate("Exports", """{"schemaVersion":"1.0","unrelated":true}""");

        LegacyExportCleanup.RemoveLegacyExportCaches(root);

        Assert.True(Directory.Exists(candidate));
    }

    [Fact]
    public void UnparseableManifestIsKept()
    {
        var candidate = WriteCandidate("export", "not json at all");

        LegacyExportCleanup.RemoveLegacyExportCaches(root);

        Assert.True(Directory.Exists(candidate));
    }

    [Fact]
    public void DeletionFailureIsReportedAndDoesNotThrow()
    {
        var candidate = WriteCandidate("Exports", """{"schemaVersion":"1.0","exportRoot":"x"}""");
        var lockedFile = Path.Combine(candidate, "plc-knowledge.db");
        File.WriteAllText(lockedFile, "cache");
        File.SetAttributes(lockedFile, FileAttributes.ReadOnly);
        File.SetAttributes(candidate, FileAttributes.ReadOnly);

        try
        {
            var notes = LegacyExportCleanup.RemoveLegacyExportCaches(root);

            Assert.Contains(notes, note =>
                note.Contains("Removed", StringComparison.Ordinal)
                || note.Contains("Could not remove", StringComparison.Ordinal));
        }
        finally
        {
            File.SetAttributes(candidate, FileAttributes.Normal);
            if (File.Exists(lockedFile))
            {
                File.SetAttributes(lockedFile, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public void MissingProjectRootIsANoOp()
    {
        var notes = LegacyExportCleanup.RemoveLegacyExportCaches(
            Path.Combine(root, "does-not-exist"));

        Assert.Empty(notes);
    }

    private string WriteCandidate(string directoryName, string manifest)
    {
        var candidate = Path.Combine(root, directoryName);
        Directory.CreateDirectory(candidate);
        File.WriteAllText(Path.Combine(candidate, "metadata.json"), manifest);
        return candidate;
    }

    public void Dispose()
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(root, recursive: true);
    }
}
