using System.Text.Json;
using LibGit2Sharp;
using Mcp.VersionControl.Git;
using Mcp.VersionControl.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Mcp.VersionControl.Tests;

public sealed class UntrackableChangeTagStoreTests : IDisposable
{
    private readonly GitFixture _fixture = new();
    private readonly VersionControlTools _tools = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void MarkerRoundTripsFromAnnotatedTag()
    {
        var commit = _fixture.CommitSource("a", "base");

        using (var repo = new Repository(_fixture.RootPath))
        {
            UntrackableChangeTagStore.Create(repo, commit);
        }

        Assert.True(RepositoryService.GetUntrackableChange(_fixture.RootPath, commit));
        using var verify = new Repository(_fixture.RootPath);
        var marker = UntrackableChangeTagStore.Read(verify, commit);
        Assert.NotNull(marker);
        Assert.Equal("1.0", marker!.SchemaVersion);
        Assert.Equal("untrackable-change", marker.Kind);
        Assert.Equal(commit, marker.CommitSha);
    }

    [Fact]
    public void ExistingMarkerTagCannotBeReplaced()
    {
        var commit = _fixture.CommitSource("a", "base");
        using var repo = new Repository(_fixture.RootPath);
        UntrackableChangeTagStore.Create(repo, commit);

        var error = Assert.Throws<VcInternalException>(() =>
            UntrackableChangeTagStore.Create(repo, commit));

        Assert.Equal("UNTRACKABLE_CHANGE_TAG_EXISTS", error.Code);
    }

    [Fact]
    public void MarkerUsesDeterministicCamelCaseJson()
    {
        var commit = _fixture.CommitSource("a", "base");
        using var repo = new Repository(_fixture.RootPath);
        UntrackableChangeTagStore.Create(repo, commit);

        var tag = Assert.Single(repo.Tags, item => item.FriendlyName == UntrackableChangeTagStore.TagName(commit));
        var expected = "{\"schemaVersion\":\"1.0\",\"commitSha\":\"" + commit + "\",\"kind\":\"untrackable-change\"}";
        Assert.Equal(expected + "\n", tag.Annotation!.Message);
    }

    [Fact]
    public void MissingTagReadsAsAbsent()
    {
        var commit = _fixture.CommitSource("a", "base");

        Assert.False(RepositoryService.GetUntrackableChange(_fixture.RootPath, commit));
    }

    [Fact]
    public void LightweightTagReadsAsAbsent()
    {
        var commit = _fixture.CommitSource("a", "base");
        using (var repo = new Repository(_fixture.RootPath))
        {
            repo.ApplyTag(UntrackableChangeTagStore.TagName(commit), commit);
        }

        Assert.False(RepositoryService.GetUntrackableChange(_fixture.RootPath, commit));
    }

    [Fact]
    public void WrongTargetTagReadsAsAbsent()
    {
        var first = _fixture.CommitSource("a", "base");
        var second = _fixture.CommitSource("b", "second");
        using (var repo = new Repository(_fixture.RootPath))
        {
            repo.ApplyTag(
                UntrackableChangeTagStore.TagName(second),
                first,
                new Signature("Test", "test@test.local", DateTimeOffset.UtcNow),
                "{\"schemaVersion\":\"1.0\",\"commitSha\":\"" + second + "\",\"kind\":\"untrackable-change\"}");
        }

        Assert.False(RepositoryService.GetUntrackableChange(_fixture.RootPath, second));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":\"9.9\",\"commitSha\":\"{0}\",\"kind\":\"untrackable-change\"}")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"commitSha\":\"{0}\",\"kind\":\"tia-state\"}")]
    [InlineData("not json at all")]
    public void InvalidPayloadReadsAsAbsent(string payloadTemplate)
    {
        var commit = _fixture.CommitSource("a", "base");
        using (var repo = new Repository(_fixture.RootPath))
        {
            repo.ApplyTag(
                UntrackableChangeTagStore.TagName(commit),
                commit,
                new Signature("Test", "test@test.local", DateTimeOffset.UtcNow),
                payloadTemplate.Replace("{0}", commit));
        }

        Assert.False(RepositoryService.GetUntrackableChange(_fixture.RootPath, commit));
    }

    [Fact]
    public void ToolsReportMarkerPresence()
    {
        var marked = _fixture.CommitSource("a", "base");
        var unmarked = _fixture.CommitSource("b", "second");
        using (var repo = new Repository(_fixture.RootPath))
        {
            UntrackableChangeTagStore.Create(repo, marked);
        }

        var markedResult = _tools.VcUntrackableChangeGet(_fixture.RootPath, marked);
        Assert.False(markedResult.IsError);
        Assert.True(Deserialize<UntrackableChangeDto>(markedResult).UntrackableChange);

        var unmarkedResult = _tools.VcUntrackableChangeGet(_fixture.RootPath, unmarked);
        Assert.False(unmarkedResult.IsError);
        Assert.False(Deserialize<UntrackableChangeDto>(unmarkedResult).UntrackableChange);
    }

    private static T Deserialize<T>(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        return JsonSerializer.Deserialize<T>(text.Text, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;
    }

    private sealed class UntrackableChangeDto
    {
        public bool UntrackableChange { get; set; }
    }
}
