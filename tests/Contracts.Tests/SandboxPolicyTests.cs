using System.Collections.Generic;
using Contracts.Sandbox;
using Xunit;

namespace Contracts.Tests;

public sealed class SandboxPolicyTests
{
    [Theory]
    [InlineData("check_environment")]
    [InlineData("list_blocks")]
    [InlineData("export_all_blocks")]
    [InlineData("query")]
    [InlineData("search")]
    [InlineData("vc_status")]
    [InlineData("vc_log")]
    [InlineData("vc_diff")]
    [InlineData("vc_branches")]
    [InlineData("src_parse_block")]
    [InlineData("src_diff")]
    [InlineData("src_validate")]
    public void KnownReadToolsClassifyAsRead(string tool)
    {
        Assert.Equal(SandboxTier.Read, new SandboxPolicy().Classify(tool));
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("disconnect")]
    [InlineData("compile_plc")]
    [InlineData("vc_init")]
    [InlineData("vc_add")]
    [InlineData("vc_commit")]
    [InlineData("vc_snapshot")]
    [InlineData("vc_config")]
    [InlineData("src_preview_edits")]
    [InlineData("src_apply_edits")]
    public void StateChangingToolsClassifyAsWrite(string tool)
    {
        Assert.Equal(SandboxTier.Write, new SandboxPolicy().Classify(tool));
    }

    [Theory]
    [InlineData("save_project")]
    [InlineData("import_block")]
    [InlineData("vc_restore")]
    public void DestructiveToolsClassifyAsDestructive(string tool)
    {
        Assert.Equal(SandboxTier.Destructive, new SandboxPolicy().Classify(tool));
    }

    [Fact]
    public void UnknownToolClassifiesAsNullSoCallersDeny()
    {
        Assert.Null(new SandboxPolicy().Classify("wipe_disk"));
    }

    [Fact]
    public void EveryCurrentMcpToolIsClassified()
    {
        string[] currentTools =
        {
            "check_environment", "list_sessions", "connect", "disconnect", "save_project", "get_project_info",
            "list_blocks", "export_block", "export_all_blocks", "export_tag_tables", "export_udts",
            "import_block", "compile_block", "compile_plc",
            "ingest_source", "query", "get_schema", "get_block", "get_network", "search",
            "vc_init", "vc_status", "vc_add", "vc_commit", "vc_log",
            "vc_diff", "vc_snapshot", "vc_restore", "vc_branches", "vc_config",
            "src_parse_block", "src_preview_edits", "src_apply_edits", "src_diff", "src_validate",
        };
        var policy = new SandboxPolicy();
        foreach (var tool in currentTools)
        {
            Assert.True(policy.Classify(tool) != null, $"{tool} has no sandbox tier");
        }
    }

    [Fact]
    public void OverridesWinOverDefaults()
    {
        var policy = new SandboxPolicy(new Dictionary<string, SandboxTier>
        {
            ["compile_plc"] = SandboxTier.Denied,
            ["custom_tool"] = SandboxTier.Read,
        });

        Assert.Equal(SandboxTier.Denied, policy.Classify("compile_plc"));
        Assert.Equal(SandboxTier.Read, policy.Classify("custom_tool"));
        Assert.Equal(SandboxTier.Read, policy.Classify("list_blocks"));
    }

    [Theory]
    [InlineData("read", SandboxTier.Read)]
    [InlineData("Write", SandboxTier.Write)]
    [InlineData("DESTRUCTIVE", SandboxTier.Destructive)]
    [InlineData("deny", SandboxTier.Denied)]
    [InlineData("nonsense", SandboxTier.Denied)] // fail closed on typos
    public void TierParsingFailsClosed(string text, SandboxTier expected)
    {
        Assert.Equal(expected, SandboxTierNames.Parse(text));
    }
}
