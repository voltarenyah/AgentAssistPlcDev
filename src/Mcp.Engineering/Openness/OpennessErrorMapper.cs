using System.Runtime.InteropServices;
using Siemens.Engineering;

namespace Mcp.Engineering.Openness;

/// <summary>Maps Openness exceptions to §8.1 error codes (mcp-engineering.md §13.5).</summary>
internal static class OpennessErrorMapper
{
    public static (string Code, string? Remediation) Map(Exception ex) => ex switch
    {
        NonRecoverableException => (
            "NON_RECOVERABLE",
            "Save your work and restart TIA Portal, then reconnect and retry the operation."),
        EngineeringException => (
            "OPENNESS_ERROR",
            "Retry the operation; if it persists, reconnect to TIA Portal."),
        COMException => (
            "TIA_NOT_INSTALLED",
            "Check that TIA Portal and its Openness installation are available."),
        _ => ("OPENNESS_ERROR", "See the engineering server logs for details."),
    };
}
