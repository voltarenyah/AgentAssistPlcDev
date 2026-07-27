using System.Text;
using System.Text.Json;
using Agent.Chat;
using Agent.Workbench;
using Mcp.Knowledge.Tools;
using Mcp.VersionControl.Git;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using Xunit;

namespace E2E.Tests;

public sealed class WorkbenchLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"workbench-e2e-{Guid.NewGuid():N}");

    [Fact]
    public void CustomRootLifecyclePreservesHistoryOverlaysDeviceDatabasesAndLegacyData()
    {
        var legacySentinel = Path.Combine(root, "legacy", "PlcAiAssistant", "exports", "sentinel.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(legacySentinel)!);
        File.WriteAllText(legacySentinel, "do-not-touch");

        var catalog = new WorkbenchCatalog(new AtomicJsonStore(), Path.Combine(root, "defaults"));
        var workbench = catalog.Create("Line 1", Path.Combine(root, "custom", "Line 1"));
        var masterRoot = Path.Combine(workbench.RootPath, "worktrees", "master");
        var shared = RepositoryService.InitShared(workbench.RootPath, masterRoot);

        var (master, devices) = RegisterMaster(catalog, workbench, masterRoot, "PLC_1", "PLC_2");
        workbench = catalog.Load(workbench.RootPath);
        var plc1 = catalog.ResolveDevice(workbench, master, devices[0]);
        var plc2 = catalog.ResolveDevice(workbench, master, devices[1]);

        Stage(plc1, "v1");
        var reconciler = new DeviceReconciler();
        var preview = reconciler.Preview(plc1);
        Assert.NotEmpty(preview.Entries);
        Assert.False(File.Exists(Path.Combine(plc1.ExportedSourceRoot, "Blocks", "Main.xml")));
        Assert.Equal("do-not-touch", File.ReadAllText(legacySentinel));

        reconciler.Apply(plc1, preview, new HashSet<string>());
        Stage(plc2, "v1");
        reconciler.Apply(plc2, reconciler.Preview(plc2), new HashSet<string>());
        RepositoryService.Add(masterRoot);
        var baselineCommit = RepositoryService.Commit(masterRoot, "initial device baselines", null);

        var baselinePath = Path.Combine(plc1.ExportedSourceRoot, "Blocks", "Main.xml");
        var baselineTime = File.GetLastWriteTimeUtc(baselinePath);
        Stage(plc1, "v1");
        var unchangedPreview = reconciler.Preview(plc1);
        Assert.All(unchangedPreview.Entries, entry =>
            Assert.Equal(ReconciliationChangeKind.Unchanged, entry.Kind));
        Assert.Empty(reconciler.Apply(
            plc1, unchangedPreview, new HashSet<string>()).ChangedPaths);
        Assert.Equal(baselineTime, File.GetLastWriteTimeUtc(baselinePath));
        Assert.Equal(baselineCommit.Sha, RepositoryService.Log(masterRoot, 1).Commits.Single().Sha);

        Ingest(plc1);
        Ingest(plc2);
        Assert.True(File.Exists(plc1.KnowledgeDbPath));
        Assert.True(File.Exists(plc2.KnowledgeDbPath));
        Assert.NotEqual(plc1.KnowledgeDbPath, plc2.KnowledgeDbPath);

        var featureRoot = Path.Combine(workbench.RootPath, "worktrees", "feature-a");
        RepositoryService.AddWorktree(shared.RepositoryPath, featureRoot, "feature-a", baselineCommit.Sha);
        var featureDevice = plc1 with
        {
            WorktreeId = "wt-feature",
            WorktreeRoot = featureRoot,
            DeviceRoot = Path.Combine(featureRoot, Path.GetRelativePath(masterRoot, plc1.DeviceRoot)),
        };
        featureDevice = featureDevice with
        {
            ExportedSourceRoot = Path.Combine(featureDevice.DeviceRoot, "exported-source"),
            ModifiedSourceRoot = Path.Combine(featureDevice.DeviceRoot, "modified-source"),
            StagingRoot = Path.Combine(featureDevice.DeviceRoot, "staging"),
            KnowledgeDbPath = Path.Combine(featureDevice.DeviceRoot, "plc-knowledge.db"),
        };

        var overlay = Path.Combine(featureDevice.ModifiedSourceRoot, "Blocks", "Main.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
        File.Copy(Path.Combine(featureDevice.ExportedSourceRoot, "Blocks", "Main.xml"), overlay);
        File.AppendAllText(overlay, Environment.NewLine);
        Update(featureDevice, "Blocks/Main.xml");
        Assert.True(File.Exists(overlay));

        var session = SessionManager.CreateNewSession(
            featureDevice, new ChatRequestSettings(), "device context");
        Assert.Equal(
            Path.Combine(featureRoot, ".automation", "sessions", session.Header.SessionId + ".json"),
            SessionManager.ResolveSessionPath(featureDevice, session.Header.SessionId));

        RepositoryService.Add(featureRoot, new[]
        {
            Path.GetRelativePath(featureRoot, overlay).Replace('\\', '/'),
        });
        var featureCommit = RepositoryService.Commit(featureRoot, "modify PLC_1 overlay", null);
        var merge = RepositoryService.Merge(masterRoot, "feature-a");

        Assert.Equal(featureCommit.Sha, merge.SourceSha);
        Assert.True(File.Exists(Path.Combine(masterRoot, Path.GetRelativePath(featureRoot, overlay))));
        Assert.Contains(
            RepositoryService.Log(masterRoot, 10).Commits,
            commit => commit.Sha == featureCommit.Sha);
        Assert.Contains(
            RepositoryService.Worktrees(shared.RepositoryPath).Worktrees,
            item => item.WorktreePath == featureRoot && item.Branch == "feature-a");
        Assert.Equal("do-not-touch", File.ReadAllText(legacySentinel));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public void DefaultCatalogRootAndTraversalProtectionAreExplicit()
    {
        var defaults = Path.Combine(root, "AutomationWorkbench", "Project");
        var catalog = new WorkbenchCatalog(new AtomicJsonStore(), defaults);
        var workbench = catalog.Create("Line:1", null);

        Assert.Equal(Path.Combine(defaults, "Line_1"), workbench.RootPath);
        Assert.Throws<WorkbenchPathException>(() =>
            WorkbenchPaths.ResolveRelative(workbench.RootPath, "../escape"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (!Directory.Exists(root))
            return;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(root, true);
    }

    private static (WorktreeMetadata Worktree, DeviceMetadata[] Devices) RegisterMaster(
        WorkbenchCatalog catalog,
        WorkbenchMetadata workbench,
        string masterRoot,
        params string[] plcNames)
    {
        var worktree = new WorktreeMetadata(
            WorkbenchSchema.CurrentVersion, "wt-master", workbench.WorkbenchId, "master",
            "master", DateTimeOffset.UtcNow.ToString("O"), null, "eng", "fixture.ap17",
            plcNames.Select((_, index) => $"dev-{index + 1}").ToArray(), null);
        var devices = plcNames.Select((name, index) => new DeviceMetadata(
            WorkbenchSchema.CurrentVersion, $"dev-{index + 1}", worktree.WorktreeId, name,
            $"fixture:{name}", null, null, null,
            new KnowledgeState(true, new Dictionary<string, string>(), null, true), [])).ToArray();
        var store = new AtomicJsonStore();
        store.Write(Path.Combine(masterRoot, "worktree.json"), worktree);
        workbench = catalog.RegisterWorktree(workbench, new(
            worktree.WorktreeId, worktree.Name, worktree.Branch, "master"));
        foreach (var device in devices)
        {
            var context = catalog.ResolveDevice(workbench, worktree, device);
            Directory.CreateDirectory(context.ExportedSourceRoot);
            Directory.CreateDirectory(context.ModifiedSourceRoot);
            Directory.CreateDirectory(context.StagingRoot);
            store.Write(Path.Combine(context.DeviceRoot, "device.json"), device);
        }
        return (worktree, devices);
    }

    private static void Stage(DeviceContext device, string version)
    {
        Directory.CreateDirectory(Path.Combine(device.StagingRoot, "Blocks"));
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Main [OB1].xml");
        File.Copy(source, Path.Combine(device.StagingRoot, "Blocks", "Main.xml"), true);
        var manifest = new
        {
            schemaVersion = "1.0",
            exportStartedUtc = "2026-07-27T00:00:00Z",
            exportFinishedUtc = "2026-07-27T00:00:01Z",
            exportRoot = device.StagingRoot,
            components = new[]
            {
                new {
                    id = "main", name = "Main", sourcePath = "Program blocks/Main",
                    category = "OB", folder = "Blocks", siemensTypeName = "OB",
                    status = "Exported", exportedFile = "Blocks/Main.xml",
                    message = (string?)null, programmingLanguage = "LAD",
                    tiaIdentifier = "Main", number = 1, isKnowHowProtected = false,
                    creationDate = version, modifiedDate = version,
                    codeModifiedDate = version, interfaceModifiedDate = version,
                },
            },
        };
        File.WriteAllText(
            Path.Combine(device.StagingRoot, "metadata.json"),
            JsonSerializer.Serialize(manifest));
    }

    private static void Ingest(DeviceContext device)
    {
        var result = new KnowledgeTools().IngestSource(
            device.ExportedSourceRoot, device.KnowledgeDbPath, device.ModifiedSourceRoot);
        Assert.NotEqual(true, result.IsError);
    }

    private static void Update(DeviceContext device, string relativePath)
    {
        if (!File.Exists(device.KnowledgeDbPath))
            Ingest(device);
        var result = new KnowledgeTools().UpdateComponents(
            device.ExportedSourceRoot, device.ModifiedSourceRoot,
            device.KnowledgeDbPath, [relativePath]);
        Assert.NotEqual(true, result.IsError);
    }
}
