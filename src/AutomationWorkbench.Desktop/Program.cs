using System.Windows.Forms;

namespace AutomationWorkbench.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "Local\\AutomationWorkbench", out var ownsMutex);
        if (!ownsMutex)
            return;

        ApplicationConfiguration.Initialize();
        var paths = RuntimePaths.Create();
        var backend = new BackendProcessHost(paths);
        using var window = new MainWindow(backend, paths);
        Application.Run(window);
    }
}
