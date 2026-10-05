using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;

/// <summary>
/// One graph scope for one device-fact request (ADR-0011): the device routes and the two compat routes
/// assemble their existing response shapes from the graph, and a device whose projection is missing —
/// a fresh or upgraded database — is projected on demand before it is served. The scope is opened once
/// per request, never once per row or per device fact.
/// </summary>
internal sealed class DeviceSnapshotGraphScope : IDisposable
{
    private readonly EngineeringGraphApiScope scope;

    internal DeviceSnapshotGraphScope(
        WorkbenchApiState state,
        EngineeringGraphApiFactory graphs,
        string workbenchId)
    {
        scope = graphs.Open(state.Workbench(workbenchId));
        Reader = new DeviceSnapshotGraphReader(scope.Service);
    }

    internal DeviceSnapshotGraphReader Reader { get; }

    public void Dispose() => scope.Dispose();
}
