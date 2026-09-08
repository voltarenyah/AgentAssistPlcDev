using System.Text.Json;
using System.Text.Json.Nodes;
using Contracts.Engineering;

namespace Agent.Workbench;

/// <summary>Safety baseline read from a device source manifest's "device" section.</summary>
public sealed record DeviceManifestSafetyBaseline(
    bool? IsSafetyDevice,
    string? ReadState,
    string? FSignature,
    IReadOnlyList<FBlockSignatureInfo>? BlockSignatures);

/// <summary>
/// The compare's safety baseline lives in the git-tracked per-device source manifest
/// (devices/&lt;plc&gt;/source/metadata.json, "device" section): the folded F-signature, its
/// read state, and the per-F-block signatures for change attribution. Every TIA export refreshes
/// the staging manifest with the live values; accepting a safety change writes them into the
/// source manifest so the next compare baselines against them. Reads are tolerant — a missing or
/// legacy manifest without safety identity yields null so callers can fall back to the legacy
/// revision.json baseline.
/// </summary>
public static class DeviceManifestSafety
{
    public const string ManifestFileName = "metadata.json";

    public static string ManifestPath(string sourceRoot) => Path.Combine(sourceRoot, ManifestFileName);

    /// <summary>Reads the safety baseline from the device source manifest; null when the
    /// manifest or its safety identity is absent (legacy manifest — the caller falls back to
    /// revision.json). An unparseable manifest degrades to null like a missing one.</summary>
    public static DeviceManifestSafetyBaseline? TryReadBaseline(string sourceRoot)
    {
        var path = ManifestPath(sourceRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("device", out var device)
                || device.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var isSafetyDevice = ReadBool(device, "isSafetyDevice");
            var readState = ReadString(device, "fSignatureReadState");
            var fSignature = ReadString(device, "fSignature");
            var blocks = ReadBlocks(device);
            if (isSafetyDevice is null && readState is null && fSignature is null && blocks is null)
            {
                return null;
            }

            return new DeviceManifestSafetyBaseline(isSafetyDevice, readState, fSignature, blocks);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Merges the live safety read into the device source manifest: the baseline
    /// advance of a safety-accepting commit. Every other manifest property is preserved; a
    /// missing or unparseable manifest is replaced by a minimal document carrying the safety
    /// section (the next export fills in the rest).</summary>
    public static void WriteSafety(string sourceRoot, PlcChecksumInfo live)
    {
        var path = ManifestPath(sourceRoot);
        JsonObject? root = null;
        if (File.Exists(path))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                root = null;
            }
        }

        root ??= new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["components"] = new JsonArray(),
        };
        if (root["device"] is not JsonObject device)
        {
            device = new JsonObject();
            root["device"] = device;
        }

        device["isSafetyDevice"] = live.IsSafetyDevice is null
            ? null
            : JsonValue.Create(live.IsSafetyDevice.Value);
        device["fSignatureReadState"] = live.FSignatureReadState;
        device["fSignature"] = live.FSignature;
        device["fBlockSignatures"] = live.FBlockSignatures is null
            ? null
            : new JsonArray(live.FBlockSignatures
                .Select(block => (JsonNode)new JsonObject
                {
                    ["path"] = block.Path,
                    ["signature"] = block.Signature,
                })
                .ToArray());

        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? ReadString(JsonElement owner, string property) =>
        owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadBool(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static IReadOnlyList<FBlockSignatureInfo>? ReadBlocks(JsonElement device)
    {
        if (!device.TryGetProperty("fBlockSignatures", out var blocks)
            || blocks.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<FBlockSignatureInfo>();
        foreach (var block in blocks.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            result.Add(new FBlockSignatureInfo
            {
                Path = ReadString(block, "path") ?? string.Empty,
                Signature = ReadString(block, "signature") ?? string.Empty,
            });
        }

        return result;
    }
}
