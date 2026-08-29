using System.Security.Cryptography;
using System.Text;

namespace Contracts.Engineering;

/// <summary>
/// Content hash of a canonical text artifact (e.g. the network configuration fingerprint):
/// SHA-256 over the exact UTF-8 text, base64url-encoded without padding — the same encoding
/// convention as <see cref="XmlContentHash"/>, but without XML normalization because the
/// producer already serializes deterministically.
/// </summary>
public static class TextContentHash
{
    /// <summary>Hash of the exact text content.</summary>
    public static string Compute(string text)
    {
        using var sha256 = SHA256.Create();
        return ToBase64Url(sha256.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Hash of a file's text content; null when the file cannot be read.</summary>
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
