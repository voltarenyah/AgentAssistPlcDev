using System.Security.Cryptography;
using System.Text;

namespace Mcp.Engineering.Export;

/// <summary>
/// Stable manifest component id: base64url(SHA256("category|sourcePath")), '=' padding trimmed.
/// Extracted from <see cref="ExportManifest"/> so the sync planner can rebuild ids without
/// touching Siemens types; the formula is byte-compatible with the reference exporter.
/// </summary>
internal static class StableId
{
    public static string Create(string category, string sourcePath)
    {
        using var sha256 = SHA256.Create();
        var input = $"{category}|{sourcePath}";
        return ToBase64Url(sha256.ComputeHash(Encoding.UTF8.GetBytes(input)));
    }

    public static string ToBase64Url(byte[] hash) =>
        Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
