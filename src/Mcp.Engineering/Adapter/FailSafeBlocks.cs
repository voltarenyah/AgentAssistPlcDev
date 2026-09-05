using Siemens.Engineering.Safety;
using Siemens.Engineering.SW.Blocks;

namespace Mcp.Engineering.Adapter;

/// <summary>
/// Detects fail-safe (F-) blocks semantically: per the TIA Portal Openness manual (§5.27.4,
/// "SafetySignatureProvider"), the SafetySignatureProvider service is present exactly on the
/// F-blocks of an F-CPU and null on any other block ("If the block is not an F-block or not a
/// block of an F-CPU S7-1200/1500, the returned signatureProvider equals to null."). Verified
/// 2026-09-02 against PEI_SinoARP_Master_V4.1.3 (CPU 1515F-2 PN): all sampled F-blocks —
/// including generated ones like FOB_SAFETY that report a non-F ProgrammingLanguage (SCL) —
/// returned the provider; standard blocks returned null. This replaces the earlier language/
/// name-prefix heuristics, which misclassified generated F-system blocks and user blocks that
/// merely start with an F-prefix.
///
/// TIA Openness refuses to export F-blocks ("The export of block '...' is not permitted.",
/// Siemens support entry 274091) and there is no ExportOptions flag to allow it, so the export
/// pipeline skips them instead of failing the whole device export. A failed service probe must
/// not skip the block: it is treated as exportable, and a true F-block is still caught by
/// <see cref="IsExportNotPermitted"/> when Openness refuses the export.
/// </summary>
internal static class FailSafeBlocks
{
    /// <summary>True when the block carries the SafetySignatureProvider service, i.e. it is an
    /// F-block of an F-CPU.</summary>
    public static bool IsFailSafe(PlcBlock block)
    {
        try
        {
            return block.GetService<SafetySignatureProvider>() is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Belt-and-braces companion to <see cref="IsFailSafe"/>: matches the exact Openness
    /// refusal ("The export of block '...' is not permitted.") so a block whose provider probe
    /// failed is still skipped instead of failing the whole device export.</summary>
    public static bool IsExportNotPermitted(Exception ex) =>
        ex.Message.IndexOf("is not permitted", StringComparison.OrdinalIgnoreCase) >= 0;
}
