using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class DeviceSnapshotReaderTests
{
    [Theory]
    [InlineData(false, false, false, "missing")]
    [InlineData(true, true, false, "stale")]
    [InlineData(true, false, true, "stale")]
    [InlineData(true, false, false, "current")]
    public void KnowledgeStateUsesDatabaseExistenceAndPersistedFlags(
        bool databaseExists,
        bool stale,
        bool baselineStale,
        string expected)
    {
        using var fixture = SnapshotFixture.Create(stale, baselineStale);
        if (databaseExists)
            File.WriteAllBytes(fixture.Context.KnowledgeDbPath, [1]);

        var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

        Assert.Equal(expected, snapshot.Knowledge.State);
        Assert.Equal(fixture.Metadata.Knowledge.UpdatedAt, snapshot.Knowledge.UpdatedAt);
    }

    private sealed class SnapshotFixture : IDisposable
    {
        private SnapshotFixture(string root, DeviceContext context, DeviceMetadata metadata)
        {
            Root = root;
            Context = context;
            Metadata = metadata;
        }

        public string Root { get; }
        public DeviceContext Context { get; }
        public DeviceMetadata Metadata { get; }

        public static SnapshotFixture Create(bool stale, bool baselineStale)
        {
            var root = Path.Combine(Path.GetTempPath(), $"device-snapshot-tests-{Guid.NewGuid():N}");
            var worktreeRoot = Path.Combine(root, "worktrees", "main");
            var deviceRoot = Path.Combine(worktreeRoot, "devices", "plc-1");
            var exportedRoot = Path.Combine(deviceRoot, "exported-source");
            var modifiedRoot = Path.Combine(deviceRoot, "modified-source");
            var stagingRoot = Path.Combine(deviceRoot, "staging");
            Directory.CreateDirectory(exportedRoot);
            Directory.CreateDirectory(modifiedRoot);
            Directory.CreateDirectory(stagingRoot);

            var context = new DeviceContext(
                "wb-1",
                "wt-1",
                "plc-1",
                root,
                worktreeRoot,
                deviceRoot,
                exportedRoot,
                modifiedRoot,
                stagingRoot,
                Path.Combine(deviceRoot, "plc-knowledge.db"));
            var metadata = new DeviceMetadata(
                WorkbenchSchema.CurrentVersion,
                "plc-1",
                "wt-1",
                "PLC 1",
                "engineering-plc-1",
                null,
                null,
                null,
                new KnowledgeState(
                    stale,
                    new Dictionary<string, string>(),
                    "2026-07-29T08:00:00Z",
                    baselineStale),
                []);

            return new SnapshotFixture(root, context, metadata);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
