using System;
using System.Text;
using System.Text.Json;

namespace Mcp.Knowledge.Tests;

/// <summary>Shared builder for PlcSourceExporter-schema "1.0" metadata.json manifests in tests.</summary>
internal static class ManifestFixtures
{
    public static void Write(TempExportTree tree, params object[] components)
    {
        var manifest = new
        {
            schemaVersion = "1.0",
            exportStartedUtc = "2026-07-18T08:00:00.0000000+00:00",
            exportFinishedUtc = "2026-07-18T08:00:01.0000000+00:00",
            exportRoot = tree.Root,
            components,
        };
        tree.AddText(
            "metadata.json",
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Realistic component entry, modeled on the D:/PEI_SinoARP example manifest.
    public static object Component(string name, string category, string exportedFile, string sourcePath)
    {
        return new
        {
            id = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{category}|{sourcePath}")).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            name,
            sourcePath,
            category,
            folder = category == "DB" ? "DB" : "Blocks",
            siemensTypeName = category,
            status = "Exported",
            exportedFile,
            message = (string?)null,
            programmingLanguage = "LAD",
            tiaIdentifier = name,
            number = 1,
            isKnowHowProtected = false,
            creationDate = "2026-07-18T05:00:00.0000000+00:00",
            modifiedDate = "2026-07-18T06:00:00.0000000+00:00",
            codeModifiedDate = "2026-07-18T06:00:00.0000000+00:00",
            interfaceModifiedDate = "2026-07-18T06:00:00.0000000+00:00",
        };
    }
}
