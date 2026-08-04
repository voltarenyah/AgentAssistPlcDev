using System.Text.Json;
using LibGit2Sharp;
using Mcp.VersionControl.Git;
using Mcp.VersionControl.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Mcp.VersionControl.Tests;

public sealed class ValidationTagStoreTests : IDisposable
{
    private readonly GitFixture _fixture = new();
    private readonly VersionControlTools _tools = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ValidationEvidenceRoundTripsFromAnnotatedTag()
    {
        var commit = _fixture.CommitSource("a", "base");
        var evidence = Evidence(commit, "tia-sync", "checksum-1");

        RepositoryService.CreateValidation(_fixture.RootPath, evidence);
        var loaded = RepositoryService.GetValidation(_fixture.RootPath, commit);

        Assert.NotNull(loaded);
        Assert.Equal("checksum-1", Assert.Single(loaded!.Devices).ProjectChecksum);
        Assert.Equal("1.0", loaded.SchemaVersion);
        Assert.Equal("tia-sync", loaded.EvidenceKind);
    }

    [Fact]
    public void ExistingValidationTagCannotBeReplaced()
    {
        var commit = _fixture.CommitSource("a", "base");
        RepositoryService.CreateValidation(_fixture.RootPath, Evidence(commit, "tia-sync", "one"));

        var error = Assert.Throws<VcInternalException>(() =>
            RepositoryService.CreateValidation(_fixture.RootPath, Evidence(commit, "tia-sync", "two")));

        Assert.Equal("VALIDATION_EXISTS", error.Code);
    }

    [Fact]
    public void ValidationEvidenceUsesDeterministicCamelCaseJson()
    {
        var commit = _fixture.CommitSource("a", "base");
        RepositoryService.CreateValidation(_fixture.RootPath, Evidence(commit, "tia-sync", "checksum-1"));

        using var repo = new Repository(_fixture.RootPath);
        var tag = Assert.Single(repo.Tags, item => item.FriendlyName == ValidationTagStore.TagName(commit));
        var expected = "{\"schemaVersion\":\"1.0\",\"evidenceKind\":\"tia-sync\",\"commitSha\":\""
            + commit
            + "\",\"workbenchId\":\"wb-1\",\"sourceWorktreeId\":null,\"confirmedAt\":\"2026-08-04T00:00:00Z\",\"confirmedBy\":\"tester\",\"machineValidated\":false,\"devices\":[{\"deviceId\":\"PLC_1\",\"plcName\":\"PLC 1\",\"projectIdentity\":\"project-1\",\"projectChecksum\":\"checksum-1\",\"objects\":[{\"identity\":\"block-1\",\"relativePath\":\"devices/PLC_1/source/A.xml\",\"sha256\":\"sha256-1\"}]}]}";
        Assert.Equal(expected + "\n", tag.Annotation!.Message);
    }

    [Fact]
    public void CreateValidationRequiresCurrentHead()
    {
        var first = _fixture.CommitSource("a", "base");
        _fixture.CommitSource("b", "second");

        var error = Assert.Throws<VcInternalException>(() =>
            RepositoryService.CreateValidation(_fixture.RootPath, Evidence(first, "tia-sync", "one")));

        Assert.Equal("VALIDATION_HEAD_REQUIRED", error.Code);
    }

    [Fact]
    public void MalformedEvidenceIsInvalid()
    {
        var commit = _fixture.CommitSource("a", "base");
        CreateRawAnnotatedTag(commit, commit, "{\"schemaVersion\":\"9.0\",\"commitSha\":\"" + commit + "\"}");

        var result = RepositoryService.GetValidation(_fixture.RootPath, commit);

        Assert.Null(result);
        var entry = Assert.Single(RepositoryService.Log(_fixture.RootPath).Commits);
        Assert.Equal(VcValidationState.Invalid, entry.ValidationState);
    }

    [Fact]
    public void WrongTargetEvidenceIsInvalid()
    {
        var first = _fixture.CommitSource("a", "base");
        var second = _fixture.CommitSource("b", "second");
        CreateRawAnnotatedTag(second, first, JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            evidenceKind = "tia-sync",
            commitSha = second,
            workbenchId = "wb-1",
            sourceWorktreeId = (string?)null,
            confirmedAt = "2026-08-04T00:00:00Z",
            confirmedBy = "tester",
            machineValidated = false,
            devices = new[] { new
            {
                deviceId = "PLC_1",
                plcName = "PLC 1",
                projectIdentity = "project-1",
                projectChecksum = "checksum-1",
                objects = Array.Empty<object>(),
            } },
        }));

        Assert.Null(RepositoryService.GetValidation(_fixture.RootPath, second));
        var entries = RepositoryService.Log(_fixture.RootPath).Commits;
        Assert.Equal(VcValidationState.Invalid, entries[0].ValidationState);
        Assert.Equal(VcValidationState.Unlabeled, entries[1].ValidationState);
    }

    [Fact]
    public void LogResolvesValidationWithoutChangingHistory()
    {
        var commit = _fixture.CommitSource("a", "base");
        RepositoryService.CreateValidation(_fixture.RootPath, Evidence(commit, "feature-merge", "checksum-1"));

        using var before = new Repository(_fixture.RootPath);
        var headBefore = before.Head.Tip!.Sha;
        var tagCountBefore = before.Tags.Count();

        var entry = Assert.Single(RepositoryService.Log(_fixture.RootPath).Commits);

        using var after = new Repository(_fixture.RootPath);
        Assert.Equal(headBefore, after.Head.Tip!.Sha);
        Assert.Equal(tagCountBefore, after.Tags.Count());
        Assert.Equal(VcValidationState.Validated, entry.ValidationState);
        Assert.Equal("feature-merge", entry.EvidenceKind);
    }

    [Fact]
    public void ValidationToolsCreateAndGetEvidence()
    {
        var commit = _fixture.CommitSource("a", "base");
        var evidence = Evidence(commit, "tia-sync", "checksum-1");

        var create = _tools.VcValidationCreate(_fixture.RootPath, evidence);
        Assert.False(create.IsError);

        var get = _tools.VcValidationGet(_fixture.RootPath, commit);
        Assert.False(get.IsError);
        var loaded = Deserialize<VcValidationEvidence>(get);
        Assert.Equal("checksum-1", Assert.Single(loaded.Devices).ProjectChecksum);
    }

    private VcValidationEvidence Evidence(string commit, string kind, string checksum) => new(
        "1.0",
        kind,
        commit,
        "wb-1",
        kind == "feature-merge" ? "wt-feature" : null,
        "2026-08-04T00:00:00Z",
        "tester",
        kind == "feature-merge",
        new[]
        {
            new VcDeviceValidation(
                "PLC_1",
                "PLC 1",
                "project-1",
                checksum,
                new[] { new VcObjectFingerprint("block-1", "devices/PLC_1/source/A.xml", "sha256-1") }),
        });

    private string CreateRawAnnotatedTag(string tagCommit, string targetCommit, string message)
    {
        using var repo = new Repository(_fixture.RootPath);
        var tag = repo.ApplyTag(
            ValidationTagStore.TagName(tagCommit),
            targetCommit,
            new Signature("Test", "test@test.local", DateTimeOffset.UtcNow),
            message);
        return tag.CanonicalName;
    }

    private static T Deserialize<T>(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        return JsonSerializer.Deserialize<T>(text.Text, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
    }
}

internal static class GitFixtureValidationExtensions
{
    public static string CommitSource(this GitFixture fixture, string content, string message)
        => fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", content, message);
}
