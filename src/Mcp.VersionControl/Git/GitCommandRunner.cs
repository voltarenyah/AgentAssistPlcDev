using System.Diagnostics;

namespace Mcp.VersionControl.Git;

internal sealed class GitCommandRunner
{
    private readonly TimeSpan timeout;

    public GitCommandRunner(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        this.timeout = timeout;
    }

    public string Run(
        string executable,
        string errorCode,
        string errorMessage,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_AUTHOR_NAME"] = "PLC Assistant";
        startInfo.Environment["GIT_AUTHOR_EMAIL"] = "assistant@plc-assistant.local";
        startInfo.Environment["GIT_COMMITTER_NAME"] = "PLC Assistant";
        startInfo.Environment["GIT_COMMITTER_EMAIL"] = "assistant@plc-assistant.local";

        using var process = Process.Start(startInfo)
            ?? throw new VcInternalException(errorCode, $"{errorMessage} Git could not be started.");
        process.StandardInput.Close();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(ToTimeoutMilliseconds(timeout)))
        {
            TryTerminate(process);
            throw new VcInternalException(
                "GIT_TIMEOUT",
                $"{errorMessage} Git timed out after {timeout.TotalSeconds:0.#} seconds.");
        }

        Task.WaitAll(standardOutput, standardError);
        if (process.ExitCode != 0)
        {
            var detail = standardError.Result.Trim();
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = standardOutput.Result.Trim();
            }

            throw new VcInternalException(
                errorCode,
                string.IsNullOrWhiteSpace(detail)
                    ? errorMessage
                    : $"{errorMessage} {detail}");
        }

        return standardOutput.Result;
    }

    private static int ToTimeoutMilliseconds(TimeSpan value) =>
        (int)Math.Min(value.TotalMilliseconds, int.MaxValue);

    private static void TryTerminate(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the timeout check and termination.
        }
    }
}
