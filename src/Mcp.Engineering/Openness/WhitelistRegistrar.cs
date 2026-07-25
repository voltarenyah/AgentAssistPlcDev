using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace Mcp.Engineering.Openness;

/// <summary>
/// Registers the calling executable in the TIA Portal Openness whitelist so that
/// the firewall/confirmation dialog is suppressed when opening TIA via Openness.
///
/// Registry target (see Siemens Openness documentation § "Whitelist"):
///   HKLM\SOFTWARE\Siemens\Automation\Openness\17.0\Whitelist\{AppName.exe}\Entry
///     Path        = REG_SZ   full path to the executable
///     DateModified = REG_SZ   last write time (UTC) in "yyyy/MM/dd HH:mm:ss.fff"
///     FileHash     = REG_SZ   SHA-256 hash of the executable, base-64 encoded
///
/// Requires admin rights (HKLM write). The <see cref="TryRegister()"/> overload
/// silently returns false when elevation is missing; standalone callers can
/// check the return value and prompt for elevation on failure.
/// </summary>
internal static class WhitelistRegistrar
{
    private const string TiaPortalVersion = "17.0";
    private const string WhitelistRoot = $@"SOFTWARE\Siemens\Automation\Openness\{TiaPortalVersion}\Whitelist";

    /// <summary>Register the entry-assembly executable — silent no-op on failure.</summary>
    public static bool TryRegister()
    {
        try
        {
            var path = Assembly.GetEntryAssembly()?.Location;
            return path is not null && TryRegister(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Register a specific executable path.</summary>
    public static bool TryRegister(string applicationPath)
    {
        try
        {
            var fileInfo = new FileInfo(applicationPath);
            if (!fileInfo.Exists)
                return false;

            // --- SHA-256 hash --------------------------------------------------
            byte[] hash;
            using (var stream = File.OpenRead(applicationPath))
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(stream);
            }
            var convertedHash = Convert.ToBase64String(hash);

            // --- Last write time (UTC) in the format Siemens expects ------------
            // Verified format from Siemens docs: "2014/06/10 15:09:44.406"
            var lastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            var lastWriteTimeFormatted = lastWriteTimeUtc.ToString(@"yyyy\/MM\/dd HH:mm:ss.fff");

            // --- Registry write (HKLM — needs admin) ---------------------------
            var appName = fileInfo.Name; // e.g. "Mcp.Engineering.exe"
            var entryKeyPath = $@"{WhitelistRoot}\{appName}\Entry";

            using var key = Registry.LocalMachine.CreateSubKey(entryKeyPath);
            if (key is null)
                return false;

            key.SetValue("Path", fileInfo.FullName, RegistryValueKind.String);
            key.SetValue("DateModified", lastWriteTimeFormatted, RegistryValueKind.String);
            key.SetValue("FileHash", convertedHash, RegistryValueKind.String);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Check whether the entry assembly is already whitelisted.</summary>
    public static bool IsRegistered()
    {
        try
        {
            var path = Assembly.GetEntryAssembly()?.Location;
            return path is not null && IsRegistered(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Check whether a specific path is already whitelisted (hash + date match).</summary>
    public static bool IsRegistered(string applicationPath)
    {
        try
        {
            var fileInfo = new FileInfo(applicationPath);

            // Compute current hash
            byte[] hash;
            using (var stream = File.OpenRead(applicationPath))
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(stream);
            }
            var currentHash = Convert.ToBase64String(hash);
            var currentDate = fileInfo.LastWriteTimeUtc.ToString(@"yyyy\/MM\/dd HH:mm:ss.fff");

            var appName = fileInfo.Name;
            var entryKeyPath = $@"{WhitelistRoot}\{appName}\Entry";

            using var key = Registry.LocalMachine.OpenSubKey(entryKeyPath);
            if (key is null)
                return false;

            var storedHash = key.GetValue("FileHash") as string;
            var storedDate = key.GetValue("DateModified") as string;

            return string.Equals(storedHash, currentHash, StringComparison.Ordinal)
                && string.Equals(storedDate, currentDate, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
