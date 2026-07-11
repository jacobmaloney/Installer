using System.Security.AccessControl;
using System.Security.Principal;

namespace Installer.Core.Services.Conduit;

/// <summary>
/// Creates %PROGRAMDATA%\Conduit locked down to Administrators + SYSTEM BEFORE
/// the service first starts, because Conduit writes admin-initial-password.txt
/// (and credential material) there on first boot. Default ProgramData ACLs let
/// any local user read new files — that would expose the generated admin
/// password. Inheritance is cut and exactly two full-control ACEs remain.
/// </summary>
public static class ConduitDataDirectorySecurer
{
    public static string DefaultDataDirectory
    {
        get
        {
            var programData = Environment.GetEnvironmentVariable("PROGRAMDATA")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Conduit");
        }
    }

    /// <summary>The exact ACEs applied: full control for Administrators and SYSTEM, inherited by all children.</summary>
    public static IReadOnlyList<FileSystemAccessRule> BuildAccessRules()
    {
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        return new[]
        {
            new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow),
            new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow)
        };
    }

    public static DirectorySecurity BuildLockedDownSecurity()
    {
        var security = new DirectorySecurity();
        // Protect from parent inheritance and do NOT copy inherited rules.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var rule in BuildAccessRules())
            security.AddAccessRule(rule);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        return security;
    }

    /// <summary>Creates (if needed) and locks down the directory. Returns null on success, error text on failure.</summary>
    public static string? Secure(string? dataDirectory = null)
    {
        var path = dataDirectory ?? DefaultDataDirectory;
        try
        {
            var directory = Directory.CreateDirectory(path);
            directory.SetAccessControl(BuildLockedDownSecurity());
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not secure '{path}': {ex.Message}";
        }
    }
}
