namespace Mcp.SourceEditor.Models;

public enum SourceEditOperation
{
    SetNetworkTitle,
    SetNetworkComment,
    SetBlockTitle,
    SetBlockComment,
    SetSafeProperty,
}

public sealed record EditTarget(string? XmlId = null, int? NetworkNumber = null);
public sealed record SourceEdit(SourceEditOperation Operation, EditTarget? Target, string? Culture, string Value, string? PropertyName = null);
public sealed record MultilingualValue(string Culture, string Value);
public sealed record NetworkInspection(int NetworkNumber, string XmlId, IReadOnlyList<MultilingualValue> Titles, IReadOnlyList<MultilingualValue> Comments);
public sealed record SourceInspection(string BlockName, string BlockType, int? BlockNumber, string? ProgrammingLanguage,
    string XmlId, IReadOnlyList<NetworkInspection> Networks, IReadOnlyList<string> SafeProperties, string Sha256);
public sealed record ValidationFinding(string Severity, string Code, string Message, string? Path = null);
public sealed record SourceValidationResult(bool IsValid, bool ProtectedContentMatches, IReadOnlyList<ValidationFinding> Findings);
public sealed record EditableFieldChange(string OwnerKind, string OwnerXmlId, int? NetworkNumber, string Field,
    string? Culture, string OldValue, string NewValue);
public sealed record SourceDiffResult(bool ProtectedContentMatches, IReadOnlyList<EditableFieldChange> Changes,
    IReadOnlyList<ValidationFinding> Findings, string OriginalSha256, string ModifiedSha256);
public sealed record NormalizedEdit(int BatchIndex, string OwnerKind, string OwnerXmlId, int? NetworkNumber,
    string Field, string? Culture, string OldValue, string NewValue);
public sealed record EditBatchResult(string SourceFilePath, string OutputFilePath, IReadOnlyList<NormalizedEdit> Edits,
    SourceValidationResult Validation, bool ProtectedContentMatches, string SourceSha256, string OutputSha256);
