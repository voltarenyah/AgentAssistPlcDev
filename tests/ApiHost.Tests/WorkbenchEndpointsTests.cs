using Agent.Workbench;
using Xunit;

public sealed class WorkbenchEndpointsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "api-workbench-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void OpenAndSelectUseImmutableIdsAndIgnoreLegacyExports()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, root);
        var created = catalog.Create("Line 1", null);
        Directory.CreateDirectory(Path.Combine(root, "PlcAiAssistant", "exports", "legacy"));

        var state = new WorkbenchApiState(catalog, store);

        Assert.Single(state.List());
        Assert.Equal(created.WorkbenchId, state.List()[0].WorkbenchId);
        state.Select(created.WorkbenchId);
        Assert.Equal(created.WorkbenchId, state.Selection!.WorkbenchId);
    }

    [Fact]
    public void UnknownApprovalCannotBeReusedOrAppliedToAnotherDevice()
    {
        var store = new AtomicJsonStore();
        var state = new WorkbenchApiState(new WorkbenchCatalog(store, root), store);
        var preview = new ReconciliationPreview("approval", "wt", "device-a", "base", "stage", []);
        state.Remember(preview);

        Assert.Throws<KeyNotFoundException>(() => state.Take("approval", "device-b"));
        Assert.Throws<KeyNotFoundException>(() => state.Take("missing", "device-a"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
