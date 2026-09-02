using Agent.Workbench;
using Contracts.Engineering;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// Network/communication fingerprint artifact ("network" key) in the hardware snapshot
/// (issue #69): baseline read, live recompute, and compare classification.
/// Note: "project" and "network" both surface as project-scope artifacts with a null device
/// name, so compare tests assert on the state sets.
/// </summary>
public sealed class HardwareConfigurationSnapshotTests : IDisposable
{
    private const string NetworkText = "network-configuration-fingerprint/v1\n[subnets]\nsubnet|PN/IE_1|Ethernet|-\n";
    private const string ChangedNetworkText = "network-configuration-fingerprint/v1\n[subnets]\nsubnet|PN/IE_2|Ethernet|-\n";

    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"hardware-snapshot-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ReadWithoutNetworkArtifactOmitsNetworkKey()
    {
        WriteManifest("""{ "projectAmlFile": "project.aml", "projectContentHash": "p1" }""");

        var snapshot = HardwareConfigurationSnapshot.Read(root);

        Assert.NotNull(snapshot);
        Assert.Equal(new[] { "project" }, snapshot.Artifacts.Keys);
    }

    [Fact]
    public void ReadRecomputesNetworkHashFromArtifactWhenPresent()
    {
        WriteManifest("""{ "projectAmlFile": "project.aml", "networkConfigurationHash": "stale" }""");
        WriteNetwork(root, NetworkText);

        var snapshot = HardwareConfigurationSnapshot.Read(root);

        Assert.NotNull(snapshot);
        Assert.Equal(TextContentHash.Compute(NetworkText), snapshot.Artifacts["network"]);
    }

    [Fact]
    public void ReadFallsBackToManifestNetworkHashWhenArtifactIsMissing()
    {
        WriteManifest("""{ "projectAmlFile": "project.aml", "networkConfigurationHash": "from-manifest" }""");

        var snapshot = HardwareConfigurationSnapshot.Read(root);

        Assert.NotNull(snapshot);
        Assert.Equal("from-manifest", snapshot.Artifacts["network"]);
    }

    [Fact]
    public void FromResultsAddsNetworkArtifactFromStagedFile()
    {
        WriteNetwork(root, NetworkText);

        var snapshot = HardwareConfigurationSnapshot.FromResults(
            new[] { new HardwareExportResult { Scope = "project", Success = true, ContentHash = "p1" } },
            root);

        Assert.Equal(TextContentHash.Compute(NetworkText), snapshot.Artifacts["network"]);
    }

    [Fact]
    public void CompareFlagsChangedNetworkFingerprintWhileProjectAmlStaysSame()
    {
        WriteManifest("""{ "projectAmlFile": "project.aml", "projectContentHash": "p1", "networkConfigurationHash": "old" }""");
        var local = HardwareConfigurationSnapshot.Read(root);
        var live = LiveSnapshot(ChangedNetworkText);

        var artifacts = HardwareConfigurationSnapshot.Compare(local, live);

        Assert.Equal(2, artifacts.Count);
        Assert.Equal("same", Assert.Single(artifacts, artifact => artifact.State == "same").State);
        var changed = Assert.Single(artifacts, artifact => artifact.State == "changed");
        Assert.Equal("project", changed.Scope);
        Assert.Null(changed.DeviceName);
    }

    [Fact]
    public void CompareFlagsMissingNetworkFingerprintWhenLiveExportLacksIt()
    {
        WriteManifest("""{ "projectAmlFile": "project.aml", "projectContentHash": "p1", "networkConfigurationHash": "old" }""");
        var local = HardwareConfigurationSnapshot.Read(root);
        var live = HardwareConfigurationSnapshot.FromResults(
            new[] { new HardwareExportResult { Scope = "project", Success = true, ContentHash = "p1" } },
            exportRoot: null);

        var artifacts = HardwareConfigurationSnapshot.Compare(local, live);

        Assert.Equal(2, artifacts.Count);
        Assert.Single(artifacts, artifact => artifact.State == "same");
        Assert.Single(artifacts, artifact => artifact.State == "missing");
    }

    [Fact]
    public void CompareTreatsIdenticalNetworkFingerprintsAsSame()
    {
        WriteManifest("""{ "projectAmlFile": "project.aml", "projectContentHash": "p1" }""");
        WriteNetwork(root, NetworkText);
        var local = HardwareConfigurationSnapshot.Read(root);
        var live = LiveSnapshot(NetworkText);

        var artifacts = HardwareConfigurationSnapshot.Compare(local, live);

        Assert.Equal(2, artifacts.Count);
        Assert.All(artifacts, artifact => Assert.Equal("same", artifact.State));
    }

    private HardwareConfigurationSnapshot LiveSnapshot(string? networkText)
    {
        string? liveRoot = null;
        if (networkText is not null)
        {
            liveRoot = Path.Combine(root, "staging");
            WriteNetwork(liveRoot, networkText);
        }
        return HardwareConfigurationSnapshot.FromResults(
            new[] { new HardwareExportResult { Scope = "project", Success = true, ContentHash = "p1" } },
            liveRoot);
    }

    private void WriteManifest(string json)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "manifest.json"), json);
    }

    private static void WriteNetwork(string directory, string text)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "network-configuration.txt"), text);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
