using System.Text.Json;
using Agent.Chat;
using Agent.Mcp;
using Xunit;

namespace Agent.Tests;

public sealed class McpToolCatalogTests
{
    private static AgentToolSpec Spec(string name, IMcpToolCaller caller, string schema = """{"type":"object","properties":{}}""") =>
        new(name, $"desc {name}", JsonDocument.Parse(schema).RootElement, caller);

    [Fact]
    public void ImportBlockIsExcluded()
    {
        var catalog = new McpToolCatalog(new[]
        {
            Spec("import_block", new FakeToolCaller()),
            Spec("search", new FakeToolCaller()),
        });

        Assert.DoesNotContain(catalog.Tools, spec => spec.Name == "import_block");
        Assert.Contains(catalog.Tools, spec => spec.Name == "search");
        Assert.Throws<KeyNotFoundException>(() => catalog.Resolve("import_block"));
    }

    [Fact]
    public void ResolveRoutesToTheRightCaller()
    {
        var engineering = new FakeToolCaller();
        var knowledge = new FakeToolCaller();
        var catalog = new McpToolCatalog(new[]
        {
            Spec("list_sessions", engineering),
            Spec("search", knowledge),
        });

        Assert.Same(engineering, catalog.Resolve("list_sessions").Caller);
        Assert.Same(knowledge, catalog.Resolve("search").Caller);
    }

    [Fact]
    public void OpenAiToolsJsonCarriesNameDescriptionAndSchema()
    {
        var catalog = new McpToolCatalog(new[]
        {
            Spec("search", new FakeToolCaller(), """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
        });

        var tools = catalog.ToOpenAiToolsJson();
        var function = Assert.Single(tools)!["function"]!;
        Assert.Equal("function", Assert.Single(tools)!["type"]!.GetValue<string>());
        Assert.Equal("search", function["name"]!.GetValue<string>());
        Assert.Equal("desc search", function["description"]!.GetValue<string>());
        Assert.Equal("string", function["parameters"]!["properties"]!["text"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void DuplicateNamesLastWins()
    {
        var first = new FakeToolCaller();
        var second = new FakeToolCaller();
        var catalog = new McpToolCatalog(new[] { Spec("search", first), Spec("search", second) });

        Assert.Same(second, catalog.Resolve("search").Caller);
        Assert.Single(catalog.Tools);
    }
}
