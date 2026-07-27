using System.Text;
using System.Text.Json;
using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class DeviceReconcilerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"device-reconciler-tests-{Guid.NewGuid():N}");

    [Fact]
    public void PreviewReportsManifestControlledChangesWithNormalizedPaths()
    {
        var fixture = CreateFixture();
        fixture.WriteBaseline("Blocks/Unchanged.xml", "same");
        fixture.WriteBaseline("Blocks/Changed.xml", "old");
        fixture.WriteBaseline("Blocks/Removed.xml", "removed");
        fixture.WriteBaselineManifest(
            Component("unchanged", @"Blocks\Unchanged.xml"),
            Component("changed", "Blocks/Changed.xml"),
            Component("removed", "Blocks/Removed.xml"));
        fixture.WriteStaging("Blocks/Unchanged.xml", "same");
        fixture.WriteStaging("Blocks/Changed.xml", "new");
        fixture.WriteStaging("Blocks/Added.xml", "added");
        fixture.WriteStagingManifest(
            Component("added", @"Blocks\Added.xml"),
            Component("changed", @"Blocks\Changed.xml"),
            Component("unchanged", "Blocks/Unchanged.xml"));

        var preview = new DeviceReconciler().Preview(fixture.Context);

        Assert.Equal("wt-1", preview.WorktreeId);
        Assert.Equal("dev-1", preview.DeviceId);
        Assert.False(string.IsNullOrWhiteSpace(preview.PreviewId));
        Assert.Equal(64, preview.BaselineTreeHash.Length);
        Assert.Equal(64, preview.StagingTreeHash.Length);
        Assert.Collection(
            preview.Entries,
            entry =>
            {
                Assert.Equal("Blocks/Added.xml", entry.RelativePath);
                Assert.Equal(ReconciliationChangeKind.Added, entry.Kind);
                Assert.Null(entry.BaselineHash);
                Assert.NotNull(entry.StagingHash);
                Assert.Equal("added", entry.ComponentIdentity);
            },
            entry =>
            {
                Assert.Equal("Blocks/Changed.xml", entry.RelativePath);
                Assert.Equal(ReconciliationChangeKind.Changed, entry.Kind);
                Assert.NotEqual(entry.BaselineHash, entry.StagingHash);
            },
            entry =>
            {
                Assert.Equal("Blocks/Removed.xml", entry.RelativePath);
                Assert.Equal(ReconciliationChangeKind.Removed, entry.Kind);
                Assert.NotNull(entry.BaselineHash);
                Assert.Null(entry.StagingHash);
                Assert.Equal("removed", entry.ComponentIdentity);
            },
            entry =>
            {
                Assert.Equal("Blocks/Unchanged.xml", entry.RelativePath);
                Assert.Equal(ReconciliationChangeKind.Unchanged, entry.Kind);
                Assert.Equal(entry.BaselineHash, entry.StagingHash);
            });
    }

    [Fact]
    public void PreviewTreeHashesAreDeterministicAcrossManifestOrderAndSeparators()
    {
        var first = CreateFixture("first");
        first.WriteStaging("Blocks/A.xml", "a");
        first.WriteStaging("Blocks/B.xml", "b");
        first.WriteStagingManifest(
            Component("b", @"Blocks\B.xml"),
            Component("a", "Blocks/A.xml"));

        var second = CreateFixture("second");
        second.WriteStaging("Blocks/A.xml", "a");
        second.WriteStaging("Blocks/B.xml", "b");
        second.WriteStagingManifest(
            Component("a", @"Blocks\A.xml"),
            Component("b", "Blocks/B.xml"));

        var reconciler = new DeviceReconciler();
        var firstPreview = reconciler.Preview(first.Context);
        var secondPreview = reconciler.Preview(second.Context);

        Assert.Equal(firstPreview.StagingTreeHash, secondPreview.StagingTreeHash);
    }

    [Fact]
    public void ApplyAtomicallyUpdatesApprovedContentWithoutTouchingIdenticalOrModifiedSource()
    {
        var fixture = CreateFixture();
        fixture.WriteBaseline("Blocks/Unchanged.xml", "same");
        fixture.WriteBaseline("Blocks/Changed.xml", "old");
        fixture.WriteBaseline("Blocks/ApprovedRemoval.xml", "delete");
        fixture.WriteBaseline("Blocks/RetainedRemoval.xml", "retain");
        fixture.WriteBaselineManifest(
            Component("unchanged", "Blocks/Unchanged.xml"),
            Component("changed", "Blocks/Changed.xml"),
            Component("delete", "Blocks/ApprovedRemoval.xml"),
            Component("retain", "Blocks/RetainedRemoval.xml"));
        fixture.WriteStaging("Blocks/Unchanged.xml", "same");
        fixture.WriteStaging("Blocks/Changed.xml", "new");
        fixture.WriteStaging("Blocks/Added.xml", "added");
        fixture.WriteStagingManifest(
            Component("unchanged", "Blocks/Unchanged.xml"),
            Component("changed", "Blocks/Changed.xml"),
            Component("added", "Blocks/Added.xml"));

        var unchangedPath = fixture.BaselinePath("Blocks/Unchanged.xml");
        var oldTimestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(unchangedPath, oldTimestamp);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Context.ModifiedSourceRoot)!);
        File.WriteAllText(fixture.Context.ModifiedSourceRoot, "must-not-be-opened");
        var modifiedTimestamp = File.GetLastWriteTimeUtc(fixture.Context.ModifiedSourceRoot);

        var reconciler = new DeviceReconciler();
        var preview = reconciler.Preview(fixture.Context);
        var outcome = reconciler.Apply(
            fixture.Context,
            preview,
            new HashSet<string>(StringComparer.Ordinal) { @"Blocks\ApprovedRemoval.xml" });

        Assert.Equal("same", File.ReadAllText(unchangedPath));
        Assert.Equal(oldTimestamp, File.GetLastWriteTimeUtc(unchangedPath));
        Assert.Equal("new", File.ReadAllText(fixture.BaselinePath("Blocks/Changed.xml")));
        Assert.Equal("added", File.ReadAllText(fixture.BaselinePath("Blocks/Added.xml")));
        Assert.False(File.Exists(fixture.BaselinePath("Blocks/ApprovedRemoval.xml")));
        Assert.True(File.Exists(fixture.BaselinePath("Blocks/RetainedRemoval.xml")));
        Assert.Equal("must-not-be-opened", File.ReadAllText(fixture.Context.ModifiedSourceRoot));
        Assert.Equal(modifiedTimestamp, File.GetLastWriteTimeUtc(fixture.Context.ModifiedSourceRoot));
        Assert.Equal(preview.PreviewId, outcome.PreviewId);
        Assert.Contains(
            outcome.ChangedPaths,
            path => path.EndsWith("exported-source/Blocks/Changed.xml", StringComparison.Ordinal));
        Assert.Contains(
            outcome.ChangedPaths,
            path => path.EndsWith("exported-source/metadata.json", StringComparison.Ordinal));
        Assert.DoesNotContain(
            outcome.ChangedPaths,
            path => path.EndsWith("exported-source/Blocks/Unchanged.xml", StringComparison.Ordinal));
        Assert.DoesNotContain(
            outcome.ChangedPaths,
            path => path.EndsWith("exported-source/Blocks/RetainedRemoval.xml", StringComparison.Ordinal));

        using (var manifest = JsonDocument.Parse(
                   File.ReadAllText(fixture.BaselinePath("metadata.json"))))
        {
            var componentIds = manifest.RootElement.GetProperty("components")
                .EnumerateArray()
                .Select(component => component.GetProperty("id").GetString())
                .ToArray();
            Assert.Contains("retain", componentIds);
            Assert.DoesNotContain("delete", componentIds);
        }

        var nextPreview = reconciler.Preview(fixture.Context);
        var retainedRemoval = Assert.Single(
            nextPreview.Entries,
            entry => entry.Kind == ReconciliationChangeKind.Removed);
        Assert.Equal("Blocks/RetainedRemoval.xml", retainedRemoval.RelativePath);
        Assert.Equal("retain", retainedRemoval.ComponentIdentity);
    }

    [Fact]
    public void ApplyRollsBackEveryTrackedMutationWhenLaterReplacementFails()
    {
        var fixture = CreateFixture();
        fixture.WriteBaseline("Blocks/A.xml", "old-a");
        fixture.WriteBaseline("Blocks/B.xml", "old-b");
        fixture.WriteBaselineManifest(
            Component("a", "Blocks/A.xml"),
            Component("b", "Blocks/B.xml"));
        fixture.WriteStaging("Blocks/A.xml", "new-a");
        fixture.WriteStaging("Blocks/B.xml", "new-b");
        fixture.WriteStagingManifest(
            Component("a", "Blocks/A.xml"),
            Component("b", "Blocks/B.xml"));
        var before = fixture.SnapshotBaseline();
        var reconciler = new DeviceReconciler(
            new FailOnNthCommittedMoveFileOperations(failureMove: 2));
        var preview = reconciler.Preview(fixture.Context);

        var exception = Assert.Throws<IOException>(() =>
            reconciler.Apply(
                fixture.Context,
                preview,
                new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal("Injected move failure.", exception.Message);
        Assert.Equal(before, fixture.SnapshotBaseline());
        Assert.DoesNotContain(
            Directory.EnumerateFiles(
                fixture.Context.ExportedSourceRoot,
                "*",
                SearchOption.AllDirectories),
            path => path.EndsWith(".tmp", StringComparison.Ordinal)
                || path.EndsWith(".bak", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyWithoutApprovedPreviewIsRejectedBeforeTrackedFilesChange()
    {
        var fixture = CreateFixture();
        fixture.WriteBaseline("Blocks/A.xml", "old");
        fixture.WriteBaselineManifest(Component("a", "Blocks/A.xml"));
        fixture.WriteStaging("Blocks/A.xml", "new");
        fixture.WriteStagingManifest(Component("a", "Blocks/A.xml"));
        var before = fixture.SnapshotBaseline();

        var exception = Assert.Throws<ReconciliationException>(() =>
            new DeviceReconciler().Apply(
                fixture.Context,
                null!,
                new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal("RECONCILIATION_APPROVAL_REQUIRED", exception.Code);
        Assert.Equal(before, fixture.SnapshotBaseline());
    }

    [Fact]
    public void ApplyRejectsChangedStagingAsStaleBeforeTrackedFilesChange()
    {
        var fixture = CreateFixture();
        fixture.WriteBaseline("Blocks/A.xml", "old");
        fixture.WriteBaselineManifest(Component("a", "Blocks/A.xml"));
        fixture.WriteStaging("Blocks/A.xml", "new");
        fixture.WriteStagingManifest(Component("a", "Blocks/A.xml"));
        var reconciler = new DeviceReconciler();
        var preview = reconciler.Preview(fixture.Context);
        var before = fixture.SnapshotBaseline();
        fixture.WriteStaging("Blocks/A.xml", "newer");

        var exception = Assert.Throws<ReconciliationException>(() =>
            reconciler.Apply(
                fixture.Context,
                preview,
                new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal("RECONCILIATION_PREVIEW_STALE", exception.Code);
        Assert.Equal(before, fixture.SnapshotBaseline());
    }

    [Fact]
    public void ApplyRejectsChangedBaselineAsStaleBeforeAnyAdditionalTrackedChange()
    {
        var fixture = CreateFixture();
        fixture.WriteBaseline("Blocks/A.xml", "old");
        fixture.WriteBaselineManifest(Component("a", "Blocks/A.xml"));
        fixture.WriteStaging("Blocks/A.xml", "new");
        fixture.WriteStagingManifest(Component("a", "Blocks/A.xml"));
        var reconciler = new DeviceReconciler();
        var preview = reconciler.Preview(fixture.Context);
        fixture.WriteBaseline("Blocks/A.xml", "external change");
        var beforeApply = fixture.SnapshotBaseline();

        var exception = Assert.Throws<ReconciliationException>(() =>
            reconciler.Apply(
                fixture.Context,
                preview,
                new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal("RECONCILIATION_PREVIEW_STALE", exception.Code);
        Assert.Equal(beforeApply, fixture.SnapshotBaseline());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ not-json")]
    [InlineData("""{"schemaVersion":"1.0","components":"not-an-array"}""")]
    [InlineData("""{"schemaVersion":"1.0","components":[{"id":"a","exportedFile":"../escape.xml"}]}""")]
    public void PreviewRejectsMissingOrMalformedStagingManifest(string? manifest)
    {
        var fixture = CreateFixture();
        if (manifest is not null)
        {
            fixture.WriteStaging("metadata.json", manifest);
        }

        var exception = Assert.Throws<ReconciliationException>(() =>
            new DeviceReconciler().Preview(fixture.Context));

        Assert.Equal("RECONCILIATION_MANIFEST_INVALID", exception.Code);
    }

    [Fact]
    public async Task DeviceOperationLockSerializesMutationsForTheSameDevice()
    {
        var operationLock = new DeviceOperationLock();
        var context = CreateFixture().Context;
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var first = operationLock.RunAsync(
            context,
            async _ =>
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
            });
        await firstEntered.Task;

        var second = operationLock.RunAsync(
            context,
            _ =>
            {
                secondEntered.SetResult();
                return Task.CompletedTask;
            });

        await Task.Delay(100);
        Assert.False(secondEntered.Task.IsCompleted);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondEntered.Task.IsCompleted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Fixture CreateFixture(string name = "default")
    {
        var fixtureRoot = Path.Combine(_root, name);
        var worktreeRoot = Path.Combine(fixtureRoot, "worktree");
        var deviceRoot = Path.Combine(worktreeRoot, "devices", "PLC_1");
        var context = new DeviceContext(
            "wb-1",
            "wt-1",
            "dev-1",
            fixtureRoot,
            worktreeRoot,
            deviceRoot,
            Path.Combine(deviceRoot, "exported-source"),
            Path.Combine(deviceRoot, "modified-source"),
            Path.Combine(deviceRoot, "staging"),
            Path.Combine(deviceRoot, "plc-knowledge.db"));
        return new Fixture(context);
    }

    private static object Component(string identity, string exportedFile) =>
        new
        {
            id = identity,
            name = identity,
            sourcePath = $"Program blocks/{identity}",
            category = "FC",
            status = "Exported",
            exportedFile,
        };

    private sealed class FailOnNthCommittedMoveFileOperations
        : IReconciliationFileOperations
    {
        private readonly int _failureMove;
        private int _committedMoves;

        public FailOnNthCommittedMoveFileOperations(int failureMove)
        {
            _failureMove = failureMove;
        }

        public bool FileExists(string path) => File.Exists(path);

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite) =>
            File.Copy(sourcePath, destinationPath, overwrite);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            if (sourcePath.EndsWith(".tmp", StringComparison.Ordinal)
                && ++_committedMoves == _failureMove)
            {
                throw new IOException("Injected move failure.");
            }

            File.Move(sourcePath, destinationPath, overwrite);
        }

        public void DeleteFile(string path) => File.Delete(path);
    }

    private sealed class Fixture
    {
        public Fixture(DeviceContext context)
        {
            Context = context;
        }

        public DeviceContext Context { get; }

        public void WriteBaseline(string relativePath, string content) =>
            Write(BaselinePath(relativePath), content);

        public void WriteStaging(string relativePath, string content) =>
            Write(StagingPath(relativePath), content);

        public void WriteBaselineManifest(params object[] components) =>
            WriteManifest(Context.ExportedSourceRoot, components);

        public void WriteStagingManifest(params object[] components) =>
            WriteManifest(Context.StagingRoot, components);

        public string BaselinePath(string relativePath) =>
            Path.Combine(
                Context.ExportedSourceRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        public string StagingPath(string relativePath) =>
            Path.Combine(
                Context.StagingRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

        public string SnapshotBaseline()
        {
            if (!Directory.Exists(Context.ExportedSourceRoot))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var file in Directory.EnumerateFiles(
                         Context.ExportedSourceRoot,
                         "*",
                         SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
            {
                builder.Append(Path.GetRelativePath(Context.ExportedSourceRoot, file))
                    .Append('\0')
                    .Append(Convert.ToHexString(File.ReadAllBytes(file)))
                    .Append('\n');
            }

            return builder.ToString();
        }

        private static void WriteManifest(string root, object[] components)
        {
            var manifest = new
            {
                schemaVersion = "1.0",
                exportStartedUtc = "2026-07-27T00:00:00.0000000+00:00",
                exportFinishedUtc = "2026-07-27T00:00:01.0000000+00:00",
                exportRoot = root,
                components,
            };
            Write(
                Path.Combine(root, "metadata.json"),
                JsonSerializer.Serialize(
                    manifest,
                    new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}
