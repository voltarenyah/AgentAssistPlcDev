using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AutomationWorkbench.Desktop;

public sealed class BackendStartupException(string message, string logPath, Exception? inner = null)
    : Exception($"{message} Backend log: {logPath}", inner)
{
    public string LogPath { get; } = logPath;
}

public sealed class BackendProcessHost : IAsyncDisposable
{
    private const int StartupTimeoutSeconds = 30;
    private const int ShutdownTimeoutSeconds = 5;
    private readonly RuntimePaths paths;
    private readonly HttpClient http;
    private readonly Func<ProcessStartInfo, Process> processStarter;
    private readonly object logGate = new();
    private Process? process;
    private string? shutdownToken;

    public BackendProcessHost(
        RuntimePaths paths,
        HttpClient? http = null,
        Func<ProcessStartInfo, Process>? processStarter = null)
    {
        this.paths = paths;
        this.http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        this.processStarter = processStarter ?? StartProcess;
    }

    public string BaseUrl => paths.BaseUrl;
    public string BackendLogPath => paths.BackendLogPath;
    public bool IsRunning => process is { HasExited: false };

    public static ProcessStartInfo CreateStartInfo(RuntimePaths paths, string shutdownToken)
    {
        var port = new Uri(paths.BaseUrl).Port.ToString();
        return new ProcessStartInfo
        {
            FileName = paths.ApiHostPath,
            WorkingDirectory = paths.InstallRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "--Application:Port", port,
                "--Application:OpenBrowserOnStart", "false",
                "--Application:ShutdownToken", shutdownToken,
            },
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return;
        if (!File.Exists(paths.ApiHostPath))
            throw new BackendStartupException($"The backend executable was not found at {paths.ApiHostPath}.", paths.BackendLogPath);

        Directory.CreateDirectory(paths.LogDirectory);
        shutdownToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var startInfo = CreateStartInfo(paths, shutdownToken);

        try
        {
            process = processStarter(startInfo);
            _ = DrainAsync(process.StandardOutput, "stdout");
            _ = DrainAsync(process.StandardError, "stderr");
            await WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not BackendStartupException)
        {
            await StopAsync().ConfigureAwait(false);
            throw new BackendStartupException("The Automation Workbench backend could not start.", paths.BackendLogPath, exception);
        }
        catch
        {
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        var current = process;
        if (current is null)
            return;

        try
        {
            if (!current.HasExited && !string.IsNullOrWhiteSpace(shutdownToken))
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{paths.BaseUrl.TrimEnd('/')}/api/lifecycle/shutdown");
                request.Headers.Add("X-AutomationWorkbench-Shutdown-Token", shutdownToken);
                using var response = await http.SendAsync(request).ConfigureAwait(false);
            }
        }
        catch
        {
            // The process fallback below is the final cleanup path.
        }

        try
        {
            if (!current.HasExited)
                await current.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(ShutdownTimeoutSeconds)).Token)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!current.HasExited)
        {
            current.Kill(entireProcessTree: true);
            await current.WaitForExitAsync().ConfigureAwait(false);
        }
        finally
        {
            current.Dispose();
            process = null;
            shutdownToken = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        http.Dispose();
    }

    private async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(StartupTimeoutSeconds));

        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (process is null || process.HasExited)
                throw new BackendStartupException("The backend exited before it became ready.", paths.BackendLogPath);

            try
            {
                using var response = await http.GetAsync($"{paths.BaseUrl.TrimEnd('/')}/api/status", timeout.Token)
                    .ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // Kestrel may still be binding its loopback listener.
            }
            catch (TaskCanceledException) when (!timeout.IsCancellationRequested)
            {
                // The short HttpClient timeout is expected during startup.
            }

            await Task.Delay(250, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync(StreamReader reader, string channel)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (logGate)
                {
                    File.AppendAllText(
                        paths.BackendLogPath,
                        $"[{DateTimeOffset.Now:O}] [{channel}] {line}{Environment.NewLine}");
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static Process StartProcess(ProcessStartInfo startInfo) =>
        Process.Start(startInfo)
        ?? throw new InvalidOperationException("Process.Start returned no backend process.");
}
