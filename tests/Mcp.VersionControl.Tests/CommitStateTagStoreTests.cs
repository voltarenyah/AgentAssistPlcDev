using LibGit2Sharp;
using Mcp.VersionControl.Git;
using Xunit;

namespace Mcp.VersionControl.Tests;

public sealed class CommitStateTagStoreTests : IDisposable
{
    private readonly GitFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void CommitStateRoundTripsWithContentFingerprint()
    {
        var commit = _fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", "a", "base");
        var devices = new[]
        {
            new VcCommitStateDevice("device-1", "PLC_1", "checksum-1", "fingerprint-1"),
        };

        RepositoryService.CreateCommitState(_fixture.RootPath, commit, "wb-1", devices);
        var loaded = RepositoryService.GetCommitState(_fixture.RootPath, commit);

        Assert.NotNull(loaded);
        Assert.Equal("1.1", loaded!.SchemaVersion);
        var device = Assert.Single(loaded.Devices);
        Assert.Equal("checksum-1", device.ProjectChecksum);
        Assert.Equal("fingerprint-1", device.ContentFingerprint);
    }

    [Fact]
    public void CommitStateWithoutFingerprintReadsBackAsNull()
    {
        var commit = _fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", "a", "base");
        var devices = new[]
        {
            new VcCommitStateDevice("device-1", "PLC_1", "checksum-1"),
        };

        RepositoryService.CreateCommitState(_fixture.RootPath, commit, "wb-1", devices);
        var loaded = RepositoryService.GetCommitState(_fixture.RootPath, commit);

        Assert.NotNull(loaded);
        Assert.Null(Assert.Single(loaded!.Devices).ContentFingerprint);
    }

    [Fact]
    public void LegacySchemaTagWithoutFingerprintStillLoads()
    {
        var commit = _fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", "a", "base");
        // Schema 1.0 tag JSON: no contentFingerprint member at all.
        var legacyJson = "{\"schemaVersion\":\"1.0\",\"commitSha\":\"" + commit
            + "\",\"workbenchId\":\"wb-1\",\"devices\":[{\"deviceId\":\"device-1\",\"plcName\":\"PLC_1\",\"projectChecksum\":\"checksum-1\"}]}";
        using (var repo = new Repository(_fixture.RootPath))
        {
            repo.ApplyTag(
                CommitStateTagStore.TagName(commit),
                commit,
                new Signature("Test", "test@test.local", DateTimeOffset.UtcNow),
                legacyJson);
        }

        var loaded = RepositoryService.GetCommitState(_fixture.RootPath, commit);

        Assert.NotNull(loaded);
        Assert.Equal("1.1", loaded!.SchemaVersion);
        var device = Assert.Single(loaded.Devices);
        Assert.Equal("checksum-1", device.ProjectChecksum);
        Assert.Null(device.ContentFingerprint);
    }
}
