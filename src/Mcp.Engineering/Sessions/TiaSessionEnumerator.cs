using Contracts.Engineering;
using Siemens.Engineering;

namespace Mcp.Engineering.Sessions;

/// <summary>
/// Wraps the Openness process-enumeration API. Any type in this namespace must only be
/// touched after <see cref="Openness.OpennessAssemblyResolver.Register"/> has run.
/// </summary>
internal static class TiaSessionEnumerator
{
    public static IReadOnlyList<SessionInfo> ListSessions()
    {
        var processes = TiaPortal.GetProcesses();
        var result = new List<SessionInfo>(processes.Count);
        foreach (var process in processes)
        {
            result.Add(new SessionInfo
            {
                Id = process.Id,
                Mode = process.Mode.ToString(),
                PortalPath = process.Path?.FullName,
                ProjectPath = process.ProjectPath?.FullName,
                AcquisitionTime = process.AcquisitionTime,
            });
        }
        // Do NOT dispose the enumerated TiaPortalProcess objects (verified 2026-07-17):
        // disposing them poisons the process-wide Openness acquisition context — every
        // later TiaPortal.GetProcess() returns null and Attach() then NREs on it, and the
        // UI portal instance exits silently minutes afterwards. They are lightweight
        // proxies; leave them to the GC.
        return result;
    }
}
