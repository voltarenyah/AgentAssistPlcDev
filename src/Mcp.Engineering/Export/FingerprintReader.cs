using Siemens.Engineering;
using Siemens.Engineering.SW;

namespace Mcp.Engineering.Export;

/// <summary>
/// Guarded read of TIA per-object fingerprints (FingerprintProvider, openness-v17-api-surface.md §10;
/// verified on PLC_1/TestPLCExportDemo 2026-07-20 via scripts/Probe-PlcChecksum.ps1). Available for
/// blocks and UDTs; null for tag tables (provider is null), inconsistent objects (GetFingerprints
/// throws RecoverableException), and know-how-protected reads. Fingerprints consider only user
/// input — never compilation results — so a save/compile cycle does not move them.
/// </summary>
internal static class FingerprintReader
{
    /// <summary>Last failure detail (diagnostics — the read is intentionally silent by default).</summary>
    internal static string? LastError { get; private set; }

    /// <summary>Canonical "Id=Value;…" string (ids sorted for stable comparison), or null.</summary>
    public static string? TryRead(IEngineeringServiceProvider obj)
    {
        try
        {
            var provider = obj.GetService<FingerprintProvider>();
            if (provider is null)
            {
                LastError = "GetService<FingerprintProvider>() returned null";
                return null;
            }

            // Materialize through the enumerator only: the Openness IList<Fingerprint>
            // implementation throws NotSupportedException("Collection is read-only") from
            // ICollection<T>.CopyTo, which LINQ buffering (OrderBy/ToList) uses when the
            // source is an ICollection<T> (verified 2026-07-20 — PowerShell foreach takes
            // the enumerator path and works).
            var fingerprints = new List<Fingerprint>();
            foreach (var fingerprint in provider.GetFingerprints())
            {
                fingerprints.Add(fingerprint);
            }

            if (fingerprints.Count == 0)
            {
                LastError = "GetFingerprints() returned no entries";
                return null;
            }

            return string.Join(";", fingerprints
                .OrderBy(fp => fp.Id.ToString(), StringComparer.Ordinal)
                .Select(fp => $"{fp.Id}={fp.Value}"));
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }
}
