namespace Mcp.SourceEditor.Xml;

public sealed class SourceEditorException : Exception
{
    public SourceEditorException(string code, string message, string? remediation = null, int? batchIndex = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Remediation = remediation;
        BatchIndex = batchIndex;
    }

    public string Code { get; }
    public string? Remediation { get; }
    public int? BatchIndex { get; }
}
