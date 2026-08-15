namespace Mcp.Engineering.Export;

/// <summary>Formats the operation-scoped count of successfully exported PLC source files.</summary>
internal sealed class ExportProgressCounter
{
    public int Count { get; private set; }

    public string NextMessage() => $"Exported PLC source files: {++Count}";
}
