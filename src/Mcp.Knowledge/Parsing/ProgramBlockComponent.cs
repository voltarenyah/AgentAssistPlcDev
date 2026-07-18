// Ported from PlcSourceExporter (src/PlcSourceExporter.Core/ProgramBlockCallGraph.cs) — adapted for mcp-knowledge; keep changes minimal to ease future re-syncs.
// Only the ProgramBlockComponent record class ports; the call-graph builder and the
// metadata.json-backed catalog stay behind (replaced by a root-element crawler in stage 2).
namespace Mcp.Knowledge.Parsing;

public sealed class ProgramBlockComponent
{
    public ProgramBlockComponent(string name, string category, string sourcePath, string exportedFile)
    {
        Name = name;
        Category = category;
        SourcePath = sourcePath;
        ExportedFile = exportedFile;
    }

    public string Name { get; }

    public string Category { get; }

    public string SourcePath { get; }

    public string ExportedFile { get; }
}
