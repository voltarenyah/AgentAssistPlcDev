using System.Security.Principal;
using System.Security;
using Microsoft.Win32;

namespace AutomationWorkbench.OpennessWhitelist;

internal static class Program
{
    private const int Success = 0;
    private const int InvalidArguments = 10;
    private const int ExecutableMissing = 11;
    private const int UnsupportedTiaVersion = 12;
    private const int ElevationRequired = 13;
    private const int RegistryWriteFailure = 14;
    private const int VerificationFailure = 15;
    private const int HashCalculationFailure = 16;

    private static int Main(string[] args)
    {
        if (args.Length != 3
            || !new[] { "register", "verify", "remove", "status" }
                .Contains(args[0], StringComparer.OrdinalIgnoreCase)
            || !string.Equals(args[1], "--exe", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(args[2]))
        {
            PrintUsage();
            return InvalidArguments;
        }

        var command = args[0].ToLowerInvariant();
        var executablePath = Path.GetFullPath(args[2]);
        if (!File.Exists(executablePath))
        {
            Console.Error.WriteLine($"Executable not found: {executablePath}");
            return ExecutableMissing;
        }

        WhitelistDocument document;
        try
        {
            document = WhitelistDocument.FromExecutable(executablePath);
        }
        catch (HashCalculationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return HashCalculationFailure;
        }

        return command switch
        {
            "register" => Register(document),
            "verify" => Verify(document),
            "remove" => Remove(document),
            "status" => Status(document),
            _ => InvalidArguments,
        };
    }

    private static int Register(WhitelistDocument document)
    {
        if (!IsElevated())
            return Report(ElevationRequired, "Administrator privileges are required to write the Siemens Openness whitelist.");

        try
        {
            using var key = OpenBaseKey().CreateSubKey(document.RegistryPath);
            if (key is null)
                return Report(RegistryWriteFailure, "The Siemens Openness whitelist registry key could not be created.");

            key.SetValue("Path", document.ExecutablePath, RegistryValueKind.String);
            key.SetValue("DateModified", document.DateModified, RegistryValueKind.String);
            key.SetValue("FileHash", document.FileHash, RegistryValueKind.String);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException)
        {
            return Report(RegistryWriteFailure, $"The Siemens Openness whitelist registry write failed: {exception.Message}");
        }

        var result = Verify(document);
        return result == Success
            ? Report(Success, "Whitelist registration succeeded.")
            : result;
    }

    private static int Verify(WhitelistDocument document)
    {
        try
        {
            using var key = OpenBaseKey().OpenSubKey(document.RegistryPath);
            if (key is null)
                return Report(VerificationFailure, "The Siemens Openness whitelist entry was not found.");

            var path = key.GetValue("Path") as string;
            var dateModified = key.GetValue("DateModified") as string;
            var fileHash = key.GetValue("FileHash") as string;
            var valuesMatch = document.ValueNames.All(name => key.GetValue(name) is string);
            if (!string.Equals(path, document.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(dateModified, document.DateModified, StringComparison.Ordinal)
                || !string.Equals(fileHash, document.FileHash, StringComparison.Ordinal)
                || !valuesMatch)
            {
                return Report(VerificationFailure, "The Siemens Openness whitelist entry does not match the executable.");
            }

            return Report(Success, "Whitelist verification succeeded.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException)
        {
            return Report(VerificationFailure, $"The Siemens Openness whitelist verification failed: {exception.Message}");
        }
    }

    private static int Status(WhitelistDocument document)
    {
        var result = Verify(document);
        Console.WriteLine($"RegistryPath={document.RegistryPath}");
        Console.WriteLine($"Path={document.ExecutablePath}");
        Console.WriteLine($"DateModified={document.DateModified}");
        Console.WriteLine($"FileHash={document.FileHash}");
        Console.WriteLine($"Registered={result == Success}");
        return result;
    }

    private static int Remove(WhitelistDocument document)
    {
        if (!IsElevated())
            return Report(ElevationRequired, "Administrator privileges are required to remove the Siemens Openness whitelist.");

        try
        {
            using var root = OpenBaseKey();
            var whitelistPath = document.RegistryPath.Substring(
                0, document.RegistryPath.LastIndexOf("\\Entry", StringComparison.Ordinal));
            root.DeleteSubKeyTree(whitelistPath, throwOnMissingSubKey: false);
            return Report(Success, "Whitelist entry removed.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or SecurityException)
        {
            return Report(RegistryWriteFailure, $"The Siemens Openness whitelist removal failed: {exception.Message}");
        }
    }

    private static RegistryKey OpenBaseKey() =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int Report(int exitCode, string message)
    {
        (exitCode == Success ? Console.Out : Console.Error).WriteLine(message);
        return exitCode;
    }

    private static void PrintUsage() =>
        Console.Error.WriteLine("Usage: AutomationWorkbench.OpennessWhitelist.exe <register|verify|remove|status> --exe <path>");
}
