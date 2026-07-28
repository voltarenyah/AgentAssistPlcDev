using Mcp.VersionControl.Git;

var runner = new GitCommandRunner(TimeSpan.FromSeconds(2));
var (executable, arguments) = ReadUntilStandardInputCloses();
Console.Write(runner.Run(
    executable,
    "TEST_FAILED",
    "The stdin test command failed.",
    arguments));

static (string Executable, string[] Arguments) ReadUntilStandardInputCloses() =>
    OperatingSystem.IsWindows()
        ? ("cmd.exe", new[] { "/d", "/s", "/c", "more > nul & echo stdin-closed" })
        : ("/bin/sh", new[] { "-c", "cat >/dev/null; echo stdin-closed" });
