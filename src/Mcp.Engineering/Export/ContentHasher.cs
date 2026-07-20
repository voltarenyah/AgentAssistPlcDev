using System.Security.Cryptography;
using System.Text;
using Contracts.Engineering;

namespace Mcp.Engineering.Export;

/// <summary>
/// Content hash of an exported XML file: SHA256 over the normalized text
/// (<see cref="XmlCompare.Normalize"/> — &lt;Created&gt; lines and CR stripped, so only real
/// content changes move it), base64url-encoded like the stable id. This is the tier-2
/// confirmation in sync_export: timestamps nominate, the hash decides (buildnote/plan/export-sync.md).
/// </summary>
internal static class ContentHasher
{
    /// <summary>Hash of the file's normalized content; null when the file cannot be read.</summary>
    public static string? TryCompute(string path)
    {
        try
        {
            return Compute(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Hash of raw XML text after normalization. Internal for tests.</summary>
    internal static string Compute(string xml)
    {
        using var sha256 = SHA256.Create();
        return StableId.ToBase64Url(sha256.ComputeHash(Encoding.UTF8.GetBytes(XmlCompare.Normalize(xml))));
    }
}
