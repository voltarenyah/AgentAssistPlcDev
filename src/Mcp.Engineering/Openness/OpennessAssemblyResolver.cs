using System.Reflection;
using Microsoft.Win32;

namespace Mcp.Engineering.Openness;

/// <summary>
/// Redirects Siemens.* assembly loading to the registry-registered PublicAPI path.
/// Must be registered before any Siemens type is touched (buildnote/plan/mcp-engineering.md §13.1).
/// Registry layout (verified 2026-07-17 on this machine):
///   HKLM\SOFTWARE\Siemens\Automation\Openness\17.0\PublicAPI\&lt;apiVersion&gt;\&lt;assemblyName&gt; = full dll path
/// </summary>
internal static class OpennessAssemblyResolver
{
    private const string TiaPortalVersion = "17.0";
    private const string TargetApiVersion = "17.0.0.0";
    private static readonly string PublicApiKeyPath =
        $@"SOFTWARE\Siemens\Automation\Openness\{TiaPortalVersion}\PublicAPI";

    public static void Register()
    {
        AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
    }

    private static Assembly? OnResolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (name is null || !name.StartsWith("Siemens.", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = ResolvePath(name);
        return path is null ? null : Assembly.LoadFrom(path);
    }

    private static string? ResolvePath(string assemblyName)
    {
        using var publicApiKey = Registry.LocalMachine.OpenSubKey(PublicApiKeyPath);
        if (publicApiKey is null)
            return null;

        // Try the API version matching the target TIA version first, then any other registered one.
        var candidates = publicApiKey.GetSubKeyNames()
            .OrderByDescending(v => string.Equals(v, TargetApiVersion, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(v => v, StringComparer.OrdinalIgnoreCase);

        foreach (var apiVersion in candidates)
        {
            using var apiKey = publicApiKey.OpenSubKey(apiVersion);
            if (apiKey is null)
                continue;

            var registered = apiKey.GetValue(assemblyName) as string;
            if (registered is not null && File.Exists(registered))
                return registered;

            // Fallback for assemblies without their own registry value: probe next to Siemens.Engineering.dll.
            var anchor = apiKey.GetValue("Siemens.Engineering") as string;
            if (anchor is not null)
            {
                var probe = Path.Combine(Path.GetDirectoryName(anchor)!, assemblyName + ".dll");
                if (File.Exists(probe))
                    return probe;
            }
        }

        return null;
    }
}
