using System.Security.Cryptography;
using System.Text;

namespace Contracts.Engineering;

/// <summary>
/// Content hash of an exported PLC XML file: SHA-256 over the normalized text
/// (<see cref="XmlCompare.Normalize"/> — export timestamp lines and CR stripped, so only real
/// content changes move it), base64url-encoded without padding. This is the shared "did the
/// content actually change" verdict: mcp-engineering stamps it as the manifest contentHash at
/// export time, and the workbench reconciler uses the same normalization when comparing the
/// baseline against a staged refresh. Without the shared normalization the raw bytes always
/// differ — every TIA export stamps fresh export timestamps — and a refresh would
/// report every component as changed even when nothing was edited.
/// </summary>
public static class XmlContentHash
{
    /// <summary>Hash of raw XML text after normalization.</summary>
    public static string Compute(string xml)
    {
        using var sha256 = SHA256.Create();
        return ToBase64Url(sha256.ComputeHash(Encoding.UTF8.GetBytes(XmlCompare.Normalize(xml))));
    }

    /// <summary>Hash of a file's normalized content; null when the file cannot be read.</summary>
    public static string? TryComputeFile(string path)
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

    private static string ToBase64Url(byte[] hash) =>
        Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
