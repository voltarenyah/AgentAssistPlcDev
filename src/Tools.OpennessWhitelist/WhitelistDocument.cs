using System.Globalization;
using System.Security.Cryptography;

namespace AutomationWorkbench.OpennessWhitelist;

public sealed record WhitelistDocument(
    string RegistryPath,
    string ExecutablePath,
    string DateModified,
    string FileHash,
    IReadOnlyList<string> ValueNames)
{
    public const string TiaPortalVersion = "17.0";
    public const string RegistryRoot = "SOFTWARE\\Siemens\\Automation\\Openness\\17.0\\Whitelist";

    public static WhitelistDocument FromExecutable(string executablePath)
    {
        var fileInfo = new FileInfo(executablePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("The executable was not found.", executablePath);

        byte[] hash;
        try
        {
            using var stream = File.OpenRead(fileInfo.FullName);
            using var sha256 = SHA256.Create();
            hash = sha256.ComputeHash(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new HashCalculationException(fileInfo.FullName, exception);
        }

        var registryPath = $"{RegistryRoot}\\{fileInfo.Name}\\Entry";
        var dateModified = fileInfo.LastWriteTimeUtc.ToString(
            "yyyy/MM/dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return new WhitelistDocument(
            registryPath,
            fileInfo.FullName,
            dateModified,
            Convert.ToBase64String(hash),
            ["Path", "DateModified", "FileHash"]);
    }

    public string RenderRegFile()
    {
        var escapedPath = ExecutablePath.Replace("\\", "\\\\");
        return string.Join(Environment.NewLine,
        [
            "Windows Registry Editor Version 5.00",
            "",
            $"[HKEY_LOCAL_MACHINE\\{RegistryPath}]",
            $"\"Path\"=\"{escapedPath}\"",
            $"\"DateModified\"=\"{DateModified}\"",
            $"\"FileHash\"=\"{FileHash}\"",
            "",
            "",
        ]);
    }
}

public sealed class HashCalculationException(string path, Exception innerException)
    : Exception($"Unable to calculate SHA-256 for '{path}'.", innerException);
