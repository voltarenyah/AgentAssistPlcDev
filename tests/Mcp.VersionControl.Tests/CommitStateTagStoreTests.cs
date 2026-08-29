using LibGit2Sharp;
using Mcp.VersionControl.Git;
using Xunit;

namespace Mcp.VersionControl.Tests;

public sealed class CommitStateTagStoreTests : IDisposable
{
    private readonly GitFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void SafetyFieldsRoundTripFromAnnotatedTag()
    {
        var commit = _fixture.CommitSource("a", "base");

        using (var repo = new Repository(_fixture.RootPath))
        {
            CommitStateTagStore.Create(repo, commit, "wb-1", new[]
            {
                new VcCommitStateDevice(
                    "device-1", "PLC_1", "checksum-1",
                    IsSafetyDevice: true,
                    FSignatureReadState: "ok",
                    FSignature: "0A1B2C3D"),
                new VcCommitStateDevice("device-2", "PLC_2", "checksum-2"),
            });
        }

        using var verify = new Repository(_fixture.RootPath);
        var evidence = CommitStateTagStore.Read(verify, commit);

        Assert.NotNull(evidence);
        Assert.Equal("1.0", evidence!.SchemaVersion);
        Assert.Equal(2, evidence.Devices.Count);
        Assert.True(evidence.Devices[0].IsSafetyDevice);
        Assert.Equal("ok", evidence.Devices[0].FSignatureReadState);
        Assert.Equal("0A1B2C3D", evidence.Devices[0].FSignature);
        Assert.Null(evidence.Devices[1].IsSafetyDevice);
        Assert.Null(evidence.Devices[1].FSignatureReadState);
        Assert.Null(evidence.Devices[1].FSignature);
    }

    [Fact]
    public void LegacyTagWithoutSafetyFieldsReadsWithNulls()
    {
        var commit = _fixture.CommitSource("a", "base");
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

        using var verify = new Repository(_fixture.RootPath);
        var evidence = CommitStateTagStore.Read(verify, commit);

        Assert.NotNull(evidence);
        var device = Assert.Single(evidence!.Devices);
        Assert.Equal("device-1", device.DeviceId);
        Assert.Equal("checksum-1", device.ProjectChecksum);
        Assert.Null(device.IsSafetyDevice);
        Assert.Null(device.FSignatureReadState);
        Assert.Null(device.FSignature);
    }
}
