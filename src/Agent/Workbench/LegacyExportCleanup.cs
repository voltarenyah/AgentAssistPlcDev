using System.Text.Json;

namespace Agent.Workbench;

/// <summary>
/// Strips legacy app export caches from a freshly imported managed TIA project before the
/// native SVN baseline commit. Older app versions wrote their own export artifacts
/// (<c>export/</c>, <c>Exports/</c> with our metadata.json manifest, knowledge databases,
/// source XML) next to the origin project; TIA Save As copies the whole project folder, so
/// those leftovers would otherwise land in the native store. They are app caches, not TIA
/// project data — TIA never reads or writes them. A candidate directory is deleted only when
/// its metadata.json is recognizably ours; anything unrecognized is left untouched, and a
/// deletion failure never aborts the import (it is reported as a note instead).
/// </summary>
public static class LegacyExportCleanup
{
    private static readonly string[] CandidateDirectoryNames = ["export", "Exports"];

    /// <summary>Removes recognized legacy export caches below <paramref name="projectRoot"/>
    /// (the directory containing the managed .ap17). Returns human-readable notes.</summary>
    public static IReadOnlyList<string> RemoveLegacyExportCaches(string projectRoot)
    {
        var notes = new List<string>();
        foreach (var name in CandidateDirectoryNames)
        {
            var candidate = Path.Combine(projectRoot, name);
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            if (!HasOurExportManifest(candidate))
            {
                notes.Add($"Kept '{candidate}': no recognized export manifest — not an app export cache.");
                continue;
            }

            try
            {
                Directory.Delete(candidate, recursive: true);
                notes.Add($"Removed legacy app export cache '{candidate}' from the managed project.");
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
            {
                notes.Add($"Could not remove legacy export cache '{candidate}': {exception.Message}");
            }
        }

        return notes;
    }

    /// <summary>Recognizes our export manifest defensively: a JSON object with a non-empty
    /// string schemaVersion plus an exportRoot string or a components array (the shape
    /// ExportMetadataDocument serializes).</summary>
    internal static bool HasOurExportManifest(string directory)
    {
        var manifestPath = Path.Combine(directory, "metadata.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(schemaVersion.GetString()))
            {
                return false;
            }

            return (root.TryGetProperty("exportRoot", out var exportRoot)
                    && exportRoot.ValueKind == JsonValueKind.String)
                || (root.TryGetProperty("components", out var components)
                    && components.ValueKind == JsonValueKind.Array);
        }
        catch (Exception exception) when (
            exception is JsonException
            or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
