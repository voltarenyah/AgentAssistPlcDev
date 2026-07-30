using Siemens.Engineering.SW.Blocks;

namespace Mcp.Engineering.Adapter;

/// <summary>
/// Detects fail-safe (F-) blocks. TIA Openness refuses to export them
/// ("The export of block '...' is not permitted.", Siemens support entry 274091) and there is
/// no ExportOptions flag to allow it, so the export pipeline skips them instead of failing the
/// whole device export. Openness reports the fail-safe dialect through ProgrammingLanguage
/// (F_LAD/F_FBD/F_DB/... — F_LAD observed in a real V17 manifest), so the "F_" prefix is the
/// reliable discriminator.
/// </summary>
internal static class FailSafeBlocks
{
    public static bool IsFailSafe(PlcBlock block) =>
        IsFailSafeLanguage(block.ProgrammingLanguage.ToString());

    public static bool IsFailSafeLanguage(string? programmingLanguage) =>
        !string.IsNullOrWhiteSpace(programmingLanguage)
        && programmingLanguage!.StartsWith("F_", StringComparison.Ordinal);

    /// <summary>
    /// Belt-and-braces companion to <see cref="IsFailSafe"/>: matches the exact Openness refusal
    /// ("The export of block '...' is not permitted.") so a block that slips past the language
    /// prefix check is still skipped instead of failing the whole device export.
    /// </summary>
    public static bool IsExportNotPermitted(Exception ex) =>
        ex.Message.IndexOf("is not permitted", StringComparison.OrdinalIgnoreCase) >= 0;
}
