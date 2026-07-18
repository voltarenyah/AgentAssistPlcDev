using System.Runtime.InteropServices;
using Siemens.Engineering;

namespace Mcp.Engineering.Openness;

/// <summary>Maps Openness exceptions to §8.1 error codes (mcp-engineering.md §13.5).</summary>
internal static class OpennessErrorMapper
{
    public static string CodeFor(Exception ex) => ex switch
    {
        NonRecoverableException => "NON_RECOVERABLE",
        EngineeringException => "OPENNESS_ERROR",
        COMException => "TIA_NOT_INSTALLED",
        _ => "OPENNESS_ERROR",
    };
}
