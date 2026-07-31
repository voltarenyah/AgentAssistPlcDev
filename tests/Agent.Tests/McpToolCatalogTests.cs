using System.Text.Json;
using Agent.Chat;
using Agent.Mcp;
using Xunit;

namespace Agent.Tests;

public sealed class McpToolCatalogTests
{
    [Fact]
    public void DuplicateToolNamesAcrossServersAreRejected()
    {
        var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        var caller = new FakeToolCaller();

        var error = Assert.Throws<InvalidOperationException>(() => new McpToolCatalog(new[]
        {
            new AgentToolSpec("same_name", "one", schema, caller, "engineering"),
            new AgentToolSpec("same_name", "two", schema, caller, "sourceeditor"),
        }));

        Assert.Contains("same_name", error.Message);
    }
    private static AgentToolSpec Spec(string name, IMcpToolCaller caller, string schema = """{"type":"object","properties":{}}""") =>
        new(name, $"desc {name}", JsonDocument.Parse(schema).RootElement, caller, "test");

    [Fact]
    public void ImportBlockIsExposed()
    {
        var importCaller = new FakeToolCaller();
        var catalog = new McpToolCatalog(new[]
        {
            Spec("import_block", importCaller),
            Spec("search", new FakeToolCaller()),
        });

        Assert.Contains(catalog.Tools, spec => spec.Name == "import_block");
        Assert.Same(importCaller, catalog.Resolve("import_block").Caller);
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
    public void DuplicateNamesFromSameServerAreRejected()
    {
        var first = new FakeToolCaller();
        var second = new FakeToolCaller();
        Assert.Throws<InvalidOperationException>(
            () => new McpToolCatalog(new[] { Spec("search", first), Spec("search", second) }));
    }
}
