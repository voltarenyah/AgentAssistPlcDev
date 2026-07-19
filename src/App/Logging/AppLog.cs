using System.IO;

namespace App.Logging;

/// <summary>
/// Persistent app log (debug support): every line shown in the UI log pane is also appended to
/// %LOCALAPPDATA%\PlcAiAssistant\logs\app-&lt;yyyyMMdd-HHmmss&gt;.log so a run can be inspected
/// after the app is closed. Logging failures never break the app.
/// </summary>
public static class AppLog
{
    private static readonly object Sync = new();
    private static string? filePath;

    /// <summary>The active log file, or null before <see cref="Initialize"/>.</summary>
    public static string? FilePath
    {
        get
        {
            lock (Sync)
            {
                return filePath;
            }
        }
    }

    /// <summary>Creates the per-launch log file under %LOCALAPPDATA%\PlcAiAssistant\logs.</summary>
    public static string Initialize()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlcAiAssistant",
            "logs");
        return Initialize(Path.Combine(directory, $"app-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
    }

    /// <summary>Creates the log file at an explicit path (used by tests).</summary>
    public static string Initialize(string path)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, string.Empty);
            filePath = path;
            return path;
        }
    }

    public static void Write(string line)
    {
        lock (Sync)
        {
            if (filePath == null)
            {
                return;
            }

            try
            {
                File.AppendAllText(filePath, line + Environment.NewLine);
            }
            catch
            {
                // logging must never break the app
            }
        }
    }
}
