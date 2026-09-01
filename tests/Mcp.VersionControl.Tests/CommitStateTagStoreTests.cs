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
            new VcCommitStateDevice("device-1", "PLC_1", "checksum-1", ContentFingerprint: "fingerprint-1"),
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
    public void SafetyFieldsRoundTripFromAnnotatedTag()
    {
        var commit = _fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", "a", "base");
        var devices = new[]
        {
            new VcCommitStateDevice(
                "device-1", "PLC_1", "checksum-1",
                IsSafetyDevice: true,
                FSignatureReadState: "ok",
                FSignature: "0A1B2C3D"),
            new VcCommitStateDevice("device-2", "PLC_2", "checksum-2"),
        };

        RepositoryService.CreateCommitState(_fixture.RootPath, commit, "wb-1", devices);
        var loaded = RepositoryService.GetCommitState(_fixture.RootPath, commit);

        Assert.NotNull(loaded);
        Assert.Equal("1.1", loaded!.SchemaVersion);
        Assert.Equal(2, loaded.Devices.Count);
        Assert.True(loaded.Devices[0].IsSafetyDevice);
        Assert.Equal("ok", loaded.Devices[0].FSignatureReadState);
        Assert.Equal("0A1B2C3D", loaded.Devices[0].FSignature);
        Assert.Null(loaded.Devices[1].IsSafetyDevice);
        Assert.Null(loaded.Devices[1].FSignatureReadState);
        Assert.Null(loaded.Devices[1].FSignature);
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

    [Fact]
    public void LegacyTagWithoutSafetyFieldsReadsWithNulls()
    {
        var commit = _fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", "a", "base");
        using (var repo = new Repository(_fixture.RootPath))
        {
            // Payload written by schema 1.0 before the safety fields existed.
            repo.ApplyTag(
                CommitStateTagStore.TagName(commit),
                commit,
                new Signature("Test", "test@test.local", DateTimeOffset.UtcNow),
                "{\"schemaVersion\":\"1.0\",\"commitSha\":\"" + commit.ToLowerInvariant()
                    + "\",\"workbenchId\":\"wb-1\",\"devices\":[{\"deviceId\":\"device-1\","
                    + "\"plcName\":\"PLC_1\",\"projectChecksum\":\"checksum-1\"}]}");
        }

        var loaded = RepositoryService.GetCommitState(_fixture.RootPath, commit);

        Assert.NotNull(loaded);
        var device = Assert.Single(loaded!.Devices);
        Assert.Equal("device-1", device.DeviceId);
        Assert.Equal("checksum-1", device.ProjectChecksum);
        Assert.Null(device.IsSafetyDevice);
        Assert.Null(device.FSignatureReadState);
        Assert.Null(device.FSignature);
        Assert.Null(device.ContentFingerprint);
    }
}
