using System.DirectoryServices.AccountManagement;
using System.Security.Principal;
using Contracts.Engineering;
using Microsoft.Win32;

namespace Mcp.Engineering.Openness;

/// <summary>
/// check_environment probe (mcp-engineering.md §8.2) — pure registry / local-group checks,
/// requires no Openness connection state.
/// </summary>
internal static class EnvironmentChecker
{
    private const string GroupName = "Siemens TIA Openness";
    private const string PublicApiKeyPath =
        @"SOFTWARE\Siemens\Automation\Openness\17.0\PublicAPI\17.0.0.0";

    public static EnvCheckResult Check()
    {
        var result = new EnvCheckResult
        {
            CurrentUser = Environment.UserName,
            MachineName = Environment.MachineName,
            OsVersion = Environment.OSVersion.ToString(),
        };

        using (var key = Registry.LocalMachine.OpenSubKey(PublicApiKeyPath))
        {
            if (key is not null)
            {
                result.OpennessVersion = key.GetValue("AssemblyVersion") as string;
                result.TiaPortalVersion = key.GetValue("EngineeringVersion") as string;
                foreach (var valueName in key.GetValueNames())
                {
                    if (!valueName.StartsWith("Siemens.", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (key.GetValue(valueName) is string path)
                        result.OpennessDllPaths[valueName] = path;
                }
                result.OpennessInstalled = result.OpennessDllPaths.Count > 0
                    && result.OpennessDllPaths.Values.All(File.Exists);
            }
        }

        result.UserInOpennessGroup = IsEffectiveMember();
        result.UserInOpennessGroupPersistent = IsPersistentMember();
        return result;
    }

    /// <summary>Membership in the current process token — what Openness access checks actually see.</summary>
    private static bool IsEffectiveMember()
    {
        try
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(GroupName);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Persistent membership in the local group on disk. Divergence from the effective
    /// check means the user was added after logon and must re-login.</summary>
    private static bool IsPersistentMember()
    {
        try
        {
            using var context = new PrincipalContext(ContextType.Machine);
            using var group = GroupPrincipal.FindByIdentity(context, GroupName);
            using var user = UserPrincipal.Current;
            return group is not null && user is not null
                && group.GetMembers(recursive: true).Any(member => member.Sid == user.Sid);
        }
        catch
        {
            return false;
        }
    }
}
