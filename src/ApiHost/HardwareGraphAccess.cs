using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;

/// <summary>
/// One graph scope for one hardware request (ADR-0011 Phase 5): the hardware routes assemble their
/// existing response shapes from the graph, and a worktree whose hardware subtree is not projected —
/// or whose stored digest no longer matches the files — is projected on demand before it is served.
/// The scope is opened once per request.
/// </summary>
internal sealed class HardwareGraphScope : IDisposable
{
    private readonly EngineeringGraphApiScope scope;

    internal HardwareGraphScope(
        WorkbenchApiState state,
        EngineeringGraphApiFactory graphs,
        string workbenchId)
    {
        scope = graphs.Open(state.Workbench(workbenchId));
        Reader = new HardwareGraphReader(scope.Service);
    }

    internal HardwareGraphReader Reader { get; }

    public void Dispose() => scope.Dispose();
}
