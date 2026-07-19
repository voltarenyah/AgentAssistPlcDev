using System.IO;
using Xunit;

namespace Agent.Tests;

public sealed class AssistantPathsTests
{
    [Fact]
    public void ExportRootSanitizesInvalidFileNameChars()
    {
        var root = AssistantPaths.ResolveExportRoot("PEI_SinoARP_Master:V4");

        Assert.EndsWith(Path.Combine("PlcAiAssistant", "exports", "PEI_SinoARP_Master_V4"), root);
    }

    [Fact]
    public void KnowledgeDbPathIsUnderExportRoot()
    {
        var dbPath = AssistantPaths.ResolveKnowledgeDbPath("TestPLC");

        Assert.EndsWith(Path.Combine("exports", "TestPLC", "plc-knowledge.db"), dbPath);
    }
}
