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
    public void FBlockSignaturesRoundTripWithPathCasingIntact()
    {
        var commit = _fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", "a", "base");
        var devices = new[]
        {
            new VcCommitStateDevice(
                "device-1", "PLC_1", "checksum-1",
                IsSafetyDevice: true,
                FSignatureReadState: "ok",
                FSignature: "fold-1",
                FBlockSignatures: new[]
                {
                    new Contracts.Engineering.FBlockSignatureInfo
                    {
                        Path = "Program blocks/Safety/F_Main",
                        Signature = "0A1B2C3D",
                    },
                }),
        };

        RepositoryService.CreateCommitState(_fixture.RootPath, commit, "wb-1", devices);
        var loaded = RepositoryService.GetCommitState(_fixture.RootPath, commit);

        var block = Assert.Single(Assert.Single(loaded!.Devices).FBlockSignatures!);
        // The store camelCases dictionary keys; the pair-list shape must keep block paths intact.
        Assert.Equal("Program blocks/Safety/F_Main", block.Path);
        Assert.Equal("0A1B2C3D", block.Signature);
    }

    [Fact]
    public void LegacyTagWithRemovedAggregateFingerprintStillLoads()
    {
        var commit = _fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", "a", "base");
        // Older tags may still contain the removed aggregate contentFingerprint member.
        var legacyJson = "{\"schemaVersion\":\"1.0\",\"commitSha\":\"" + commit
            + "\",\"workbenchId\":\"wb-1\",\"devices\":[{\"deviceId\":\"device-1\",\"plcName\":\"PLC_1\",\"projectChecksum\":\"checksum-1\",\"contentFingerprint\":\"legacy-fingerprint\"}]}";
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
    }
}
