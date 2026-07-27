namespace Contracts.Knowledge;

public sealed record KnowledgeUpdateResult
{
    public KnowledgeUpdateResult(
        string dbPath,
        string[] updatedComponents,
        IReadOnlyDictionary<string, string> appliedHashes,
        string[] warnings)
    {
        DbPath = dbPath;
        UpdatedComponents = updatedComponents;
        AppliedHashes = appliedHashes;
        Warnings = warnings;
    }

    public string DbPath { get; }

    public string[] UpdatedComponents { get; }

    public IReadOnlyDictionary<string, string> AppliedHashes { get; }

    public string[] Warnings { get; }
}
