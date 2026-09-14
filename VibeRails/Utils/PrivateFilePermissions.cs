using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace VibeRails.Utils;

/// <summary>
/// Restricts VibeRails data that can contain API keys, terminal transcripts, repository paths and
/// session state to the owning user.
///
/// Windows used to be a no-op here, on the reasoning that the user-profile ACL was enough. It is
/// not: <c>~/.vibe_rails</c> inherits whatever the profile root grants, and development tools
/// (AI-agent sandboxes, container runtimes, sync clients) routinely add an ACE to directories under
/// it. We were *validating* that ACL in one place and never *setting* it anywhere — so the only
/// available response to a bad grant was to refuse to work. Now the directory is hardened to match
/// what the Unix branch has always done (0700).
/// </summary>
internal static class PrivateFilePermissions
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            HardenWindowsDirectory(path);
            return;
        }

        File.SetUnixFileMode(path, PrivateDirectoryMode);
    }

    internal static void EnsureFile(string path)
    {
        // Files inherit the hardened directory ACL on Windows, so there is nothing to set per file.
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }

    /// <summary>
    /// Replaces the directory's DACL with an explicit, non-inheriting one granting Full Control to
    /// the current user, SYSTEM and the local Administrators group, and to nobody else.
    ///
    /// The DACL is built from scratch rather than edited in place. Editing would mean calling
    /// <c>SetAccessRuleProtection(isProtected: true, preserveInheritance: false)</c> on a directory
    /// whose rules are all inherited, which strips every rule and can leave a DACL that denies the
    /// owner their own data. Constructing the three rules first makes the result deterministic and
    /// makes it impossible to lock the user out.
    ///
    /// Administrators and SYSTEM are included deliberately: an administrator can take ownership
    /// regardless, so excluding them buys no security and breaks backup, AV and repair tooling.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void HardenWindowsDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);

            // Cheap early-out. Protected rules mean a previous run already did this, so the common
            // case costs one ACL read rather than a write on every process start.
            if (directory.GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected)
                return;

            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not { } currentUser)
                return;

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var principal in new[]
                     {
                         currentUser,
                         new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, domainSid: null)
                     })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    principal,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            directory.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or
                                   PlatformNotSupportedException or PrivilegeNotHeldException or
                                   System.Security.SecurityException)
        {
            // Best effort only. A directory we cannot re-permission (a redirected profile, a
            // policy-managed share) must never stop VibeRails from starting — this runs from
            // Program.cs before anything else, including logging.
        }
    }
}
