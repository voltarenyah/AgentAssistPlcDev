namespace Contracts.Engineering;

/// <summary>
/// Category argument of the generalized per-object platform operations
/// (<see cref="IEngineeringPlatform.OpenSourceObjectInEditor"/> and
/// <see cref="IEngineeringPlatform.ExportSourceObject"/>): the block kinds OB/FB/FC/DB plus
/// Tags (PLC tag table) and UDT (PLC data type). Canonical values match the export manifest
/// categories (OB/FB/FC/DB/Tags/UDT).
/// </summary>
public static class SourceObjectCategory
{
    public const string Tags = "Tags";
    public const string Udt = "UDT";

    /// <summary>Canonical category (OB/FB/FC/DB/Tags/UDT), or null when the value is unknown.</summary>
    public static string? Normalize(string? category) =>
        category?.Trim().ToUpperInvariant() switch
        {
            "OB" or "FB" or "FC" or "DB" => category!.Trim().ToUpperInvariant(),
            "TAGS" => Tags,
            "UDT" => Udt,
            _ => null,
        };

    /// <summary>True for the block kinds (OB/FB/FC/DB) — these resolve through the block group.</summary>
    public static bool IsBlockKind(string category) =>
        category is "OB" or "FB" or "FC" or "DB";
}
