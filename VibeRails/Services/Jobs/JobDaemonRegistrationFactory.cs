using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using VibeRails.Daemon;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

/// <summary>
/// Resolves the one stable command that an operating-system registration may persist for VBD.
/// Lifecycle calls can originate from a temporary installer payload, but the resulting task/unit/
/// LaunchAgent must always point back to the current user's ~/.vibe_rails installation.
/// </summary>
internal static class JobDaemonRegistrationFactory
{
    internal const string ApplicationId = "viberails-daemon";
    internal const string ProcessArgument = "--job-daemon";
    private const string TrustedInstallerSid =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    public static JobDaemonRegistrationResolution Resolve()
    {
        var installDirectory = Path.GetFullPath(PathConstants.GetInstallDirPath());
        var profileDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profileDirectory))
        {
            return JobDaemonRegistrationResolution.Unavailable(
                installDirectory,
                "Unable to locate the current user's profile directory.");
        }

        var expectedDirectory = Path.GetFullPath(Path.Combine(
            profileDirectory,
            PathConstants.DEFAULT_INSTALL_DIR_NAME));
        if (!PathEquals(installDirectory, expectedDirectory))
        {
            return JobDaemonRegistrationResolution.Unavailable(
                installDirectory,
                "VBD can only be registered from the stable ~/.vibe_rails installation.");
        }

        var directoryError = ValidateStablePath(installDirectory, "installation directory");
        if (directoryError is not null)
            return JobDaemonRegistrationResolution.Unavailable(installDirectory, directoryError);

        var resolution = ResolveStableCommand(
            installDirectory,
            OperatingSystem.IsWindows(),
            Environment.ProcessPath,
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"));
        if (resolution.LaunchTargetExists && resolution.LaunchTargetPath is not null)
        {
            var targetError = ValidateStablePath(resolution.LaunchTargetPath, "launch target");
            if (targetError is not null)
            {
                return JobDaemonRegistrationResolution.Unavailable(
                    installDirectory,
                    targetError,
                    resolution.LaunchTargetPath);
            }
        }

        return resolution;
    }

    internal static JobDaemonRegistrationResolution ResolveStableCommand(
        string installDirectory,
        bool windows,
        string? currentProcessPath,
        string? configuredDotnetHostPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);
        installDirectory = Path.GetFullPath(installDirectory);

        var nativePath = Path.Combine(installDirectory, windows ? "vb.exe" : "vb");
        var managedPath = Path.Combine(installDirectory, "vb.dll");

        string executablePath;
        string launchTargetPath;
        string[] arguments;

        // Official releases are native executables. Prefer that stable target even when this code
        // is running from an installer staging directory and the live target is temporarily absent.
        if (File.Exists(nativePath) || !File.Exists(managedPath))
        {
            executablePath = nativePath;
            launchTargetPath = nativePath;
            arguments = [ProcessArgument];
        }
        else
        {
            var dotnetHost = ResolveDotnetHost(currentProcessPath, configuredDotnetHostPath);
            if (dotnetHost.Path is null)
            {
                return JobDaemonRegistrationResolution.Unavailable(
                    installDirectory,
                    dotnetHost.Error ??
                        "The stable vb.dll installation exists, but an absolute dotnet host path could not be resolved.",
                    managedPath);
            }

            executablePath = dotnetHost.Path;
            launchTargetPath = managedPath;
            arguments = [managedPath, ProcessArgument];
        }

        var registration = new DaemonRegistration(
            ApplicationId,
            "VibeRails Demon",
            executablePath,
            arguments,
            installDirectory,
            installDirectory,
            description: "Runs VibeRails Automations for the current user while the dashboard is closed.",
            windowsTaskBaseName: "VibeRailsDemon",
            systemdUnitBaseName: "viberails-demon",
            launchAgentLabelBase: "ai.viberails.demon",
            legacyRegistrations: DaemonLegacyRegistrations.VibeRailsJobs);

        return new JobDaemonRegistrationResolution(
            registration,
            installDirectory,
            launchTargetPath,
            File.Exists(launchTargetPath),
            Error: null);
    }

    public static string GetPipeName(ICurrentUserIdentityProvider identityProvider)
    {
        ArgumentNullException.ThrowIfNull(identityProvider);
        return GetPipeName(identityProvider.GetCurrent());
    }

    public static string GetPipeName(CurrentUserIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return $"{ApplicationId}-{identity.ScopeKey}-ipc";
    }

    private static (string? Path, string? Error) ResolveDotnetHost(
        string? currentProcessPath,
        string? configuredDotnetHostPath)
    {
        string? firstValidationError = null;
        foreach (var candidate in new[] { configuredDotnetHostPath, currentProcessPath })
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
                continue;

            var fileName = Path.GetFileNameWithoutExtension(candidate);
            if (!fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                continue;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                firstValidationError ??= $"The dotnet host path is invalid: {ex.Message}";
                continue;
            }

            var validationError = ValidateDotnetHostPath(fullPath);
            if (validationError is null)
                return (fullPath, null);
            firstValidationError ??= validationError;
        }

        return (null, firstValidationError ??
            "The stable vb.dll installation exists, but an absolute dotnet host path could not be resolved.");
    }

    private static string? ValidateDotnetHostPath(string path)
    {
        if (!File.Exists(path))
            return $"The dotnet host path does not exist: {path}";
        if (IsTransientDotnetHostPath(path))
            return $"The dotnet host cannot be registered from a temporary or staging path: {path}";

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return $"The dotnet host cannot be a symbolic link or reparse point: {path}";

            if (OperatingSystem.IsWindows())
                return ValidateWindowsDotnetHost(path);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return ValidateUnixDotnetHost(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or System.Security.SecurityException)
        {
            return $"The dotnet host could not be validated: {ex.Message}";
        }
    }

    private static bool IsTransientDotnetHostPath(string path)
    {
        var temporaryRoots = new[]
        {
            Path.GetTempPath(),
            Environment.GetEnvironmentVariable("TEMP"),
            Environment.GetEnvironmentVariable("TMP"),
            Environment.GetEnvironmentVariable("TMPDIR"),
            OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? string.Empty, "Temp")
                : "/tmp",
            OperatingSystem.IsWindows() ? null : "/var/tmp",
            OperatingSystem.IsMacOS() ? "/private/tmp" : null
        };
        if (temporaryRoots.Any(root =>
                !string.IsNullOrWhiteSpace(root) &&
                Path.IsPathFullyQualified(root) &&
                IsPathWithin(path, root)))
        {
            return true;
        }

        string[] transientSegments =
        [
            "tmp", ".tmp", "temp", ".temp", "stage", ".stage", "staging", ".staging",
            "installer-stage", "installer-staging"
        ];
        var pathRoot = Path.GetPathRoot(path) ?? string.Empty;
        return Path.GetRelativePath(pathRoot, path)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .SkipLast(1)
            .Any(segment => transientSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsPathWithin(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(fullPath, fullDirectory, comparison))
            return true;

        var prefix = Path.EndsInDirectorySeparator(fullDirectory)
            ? fullDirectory
            : fullDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, comparison);
    }

    [SupportedOSPlatform("windows")]
    private static string? ValidateWindowsDotnetHost(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentSid = identity.User
            ?? throw new InvalidOperationException("Unable to determine the current Windows user SID.");
        var security = FileSystemAclExtensions.GetAccessControl(
            new FileInfo(path),
            AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrustedWindowsPrincipal(owner, currentSid))
            return $"The dotnet host is not owned by the current user or a trusted system principal: {path}";

        return GrantsUntrustedWindowsWriteAccess(security, currentSid)
            ? $"The dotnet host grants write access to an untrusted principal: {path}"
            : null;
    }

    [SupportedOSPlatform("windows")]
    private static bool GrantsUntrustedWindowsWriteAccess(
        FileSystemSecurity security,
        SecurityIdentifier currentSid)
    {
        const FileSystemRights mutationRights =
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.WriteExtendedAttributes |
            FileSystemRights.WriteAttributes |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;
        var accessRules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in accessRules)
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.FileSystemRights & mutationRights) == 0 ||
                rule.IdentityReference is not SecurityIdentifier sid ||
                IsTrustedWindowsPrincipal(sid, currentSid))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsTrustedWindowsPrincipal(
        SecurityIdentifier sid,
        SecurityIdentifier currentSid) =>
        sid.Equals(currentSid) ||
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
        sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid) ||
        sid.Value.Equals("S-1-3-4", StringComparison.Ordinal) || // OWNER RIGHTS
        sid.Value.Equals(TrustedInstallerSid, StringComparison.Ordinal);

    [UnsupportedOSPlatform("windows")]
    private static string? ValidateUnixDotnetHost(string path)
    {
        var currentUid = new CurrentUserIdentityProvider().GetCurrent().UnixUserId
            ?? throw new InvalidOperationException("Unable to determine the current Unix user id.");
        if (!TryReadUnixOwner(path, out var ownerUid) || (ownerUid != currentUid && ownerUid != 0))
            return $"The dotnet host is not owned by the current user or root: {path}";

        var mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            return $"The dotnet host must not be writable by other users: {path}";
        return null;
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? ValidateStablePath(string path, string description)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            return null;

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return $"The stable VBD {description} cannot be a symbolic link or reparse point: {path}";

            if (OperatingSystem.IsWindows())
                return ValidateWindowsOwner(path, description, attributes);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return ValidateUnixOwnerAndMode(path, description, attributes);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   InvalidOperationException or System.Security.SecurityException)
        {
            return $"The stable VBD {description} could not be validated: {ex.Message}";
        }
    }

    private static string? ValidateWindowsOwner(
        string path,
        string description,
        FileAttributes attributes)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();

        using var identity = WindowsIdentity.GetCurrent();
        var currentSid = identity.User
            ?? throw new InvalidOperationException("Unable to determine the current Windows user SID.");
        FileSystemSecurity security = (attributes & FileAttributes.Directory) != 0
            ? FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access)
            : FileSystemAclExtensions.GetAccessControl(
                new FileInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !owner.Equals(currentSid))
            return $"The stable VBD {description} is not owned by the current user: {path}";

        // Owner alone is not enough: Task Scheduler will later execute this target as the
        // current user, so another local account with a write/delete/reset-ACL grant could
        // replace it first. Apply the same untrusted-writer rule the managed dotnet host uses.
        return GrantsUntrustedWindowsWriteAccess(security, currentSid)
            ? $"The stable VBD {description} grants write access to an untrusted principal: {path}"
            : null;
    }

    [UnsupportedOSPlatform("windows")]
    private static string? ValidateUnixOwnerAndMode(
        string path,
        string description,
        FileAttributes attributes)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException();

        var currentUid = new CurrentUserIdentityProvider().GetCurrent().UnixUserId
            ?? throw new InvalidOperationException("Unable to determine the current Unix user id.");
        if (!TryReadUnixOwner(path, out var ownerUid) || ownerUid != currentUid)
            return $"The stable VBD {description} is not owned by the current user: {path}";

        var mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            return $"The stable VBD {description} must not be writable by other users: {path}";
        return null;
    }

    private static bool TryReadUnixOwner(string path, out uint ownerUid)
    {
        ownerUid = 0;
        var statPath = File.Exists("/usr/bin/stat")
            ? "/usr/bin/stat"
            : File.Exists("/bin/stat")
                ? "/bin/stat"
                : null;
        if (statPath is null)
            return false;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = statPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        if (OperatingSystem.IsMacOS())
        {
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("%u");
        }
        else
        {
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add("%u");
            process.StartInfo.ArgumentList.Add("--");
        }
        process.StartInfo.ArgumentList.Add(path);

        if (!process.Start() || !process.WaitForExit(2000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return false;
        }

        var output = process.StandardOutput.ReadToEnd().Trim();
        return process.ExitCode == 0 &&
            uint.TryParse(output, NumberStyles.None, CultureInfo.InvariantCulture, out ownerUid);
    }
}

internal sealed record JobDaemonRegistrationResolution(
    DaemonRegistration? Registration,
    string InstallDirectory,
    string? LaunchTargetPath,
    bool LaunchTargetExists,
    string? Error)
{
    public static JobDaemonRegistrationResolution Unavailable(
        string installDirectory,
        string error,
        string? launchTargetPath = null) =>
        new(null, Path.GetFullPath(installDirectory), launchTargetPath, false, error);
}

internal interface IJobDaemonRegistrationProvider
{
    JobDaemonRegistrationResolution Current { get; }
}

/// <summary>
/// Re-resolving cache around <see cref="JobDaemonRegistrationFactory.Resolve"/>. A one-time
/// singleton resolution latches "vb.exe is missing" for the whole dashboard lifetime, leaving the
/// VBD panel Unavailable even after the user installs the payload. This provider keeps the cheap
/// happy path (one File.Exists per read) but re-resolves whenever the previous answer was
/// unusable or the launch target's presence changed in either direction.
/// </summary>
internal sealed class JobDaemonRegistrationProvider(
    Func<JobDaemonRegistrationResolution>? resolve = null) : IJobDaemonRegistrationProvider
{
    private readonly Func<JobDaemonRegistrationResolution> _resolve =
        resolve ?? JobDaemonRegistrationFactory.Resolve;
    private readonly object _gate = new();
    private JobDaemonRegistrationResolution? _cached;

    public JobDaemonRegistrationResolution Current
    {
        get
        {
            lock (_gate)
            {
                if (_cached is { Registration: not null, LaunchTargetExists: true } cached &&
                    File.Exists(cached.LaunchTargetPath))
                {
                    return cached;
                }

                _cached = _resolve();
                return _cached;
            }
        }
    }
}
