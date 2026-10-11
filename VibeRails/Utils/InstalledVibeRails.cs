using System.Diagnostics;
using Serilog;

namespace VibeRails.Utils;

/// <summary>
/// The one answer to "which <c>vb</c> should another program call back into": the per-user
/// install (<c>~/.vibe_rails/vb[.exe]</c>) whenever it exists, otherwise the running binary.
/// </summary>
/// <remarks>
/// install.ps1 and install.sh overlay that file in place and put its directory on PATH, so a path
/// recorded in someone else's configuration (a CLI's MCP registration, a repository's git hooks)
/// keeps working across upgrades and always runs whatever the user has installed. The VS Code
/// bundle lives under a version-numbered extension directory; a recorded path into it goes stale
/// on every extension update and dead once VS Code removes the old directory, which is how a Codex
/// session kept running a build without the Board review-capture fix (VB-K5SCD-210). The install
/// is the source of truth on purpose: it is what the user updates, and it is on PATH.
/// </remarks>
public static class InstalledVibeRails
{
    /// <summary>The installer's fixed location, independent of the data-directory override; the install scripts only know this path.</summary>
    public static string? InstallExecutablePath()
    {
        try
        {
            return Path.Combine(PathConstants.GetInstallDirPath(), OperatingSystem.IsWindows() ? "vb.exe" : "vb");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The executable another program should record for calling VibeRails back. A running
    /// <c>vb</c> binary is replaced by the install when one exists; anything else (the dotnet host
    /// of a framework-dependent build, an empty path) is returned unchanged for the caller's own
    /// fallback logic. A version difference between the two is logged, never a reason to fall back.
    /// </summary>
    public static string? ExecutableForCallbacks(string? processPath, string? installPath = null)
    {
        if (!IsVibeRailsExecutable(processPath))
        {
            return processPath;
        }

        installPath ??= InstallExecutablePath();
        if (installPath is null || !File.Exists(installPath))
        {
            return processPath;
        }

        if (!string.Equals(Path.GetFullPath(installPath), Path.GetFullPath(processPath!), StringComparison.OrdinalIgnoreCase))
        {
            var installVersion = ProductVersionOf(installPath);
            var runningVersion = ProductVersionOf(processPath!);
            if (!string.Equals(installVersion, runningVersion, StringComparison.Ordinal))
            {
                Log.Warning(
                    "[Install] Recording {InstallPath} ({InstallVersion}) for callbacks while this process runs {RunningPath} ({RunningVersion}). The install is the source of truth; update it if the difference is unexpected.",
                    installPath, installVersion ?? "unknown", processPath, runningVersion ?? "unknown");
            }
        }

        return installPath;
    }

    /// <summary>True for an existing file named <c>vb</c> (any extension), the published executable.</summary>
    public static bool IsVibeRailsExecutable(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && File.Exists(path)
            && Path.GetFileNameWithoutExtension(path).Equals("vb", StringComparison.OrdinalIgnoreCase);

    /// <summary>The PE product version (includes the commit suffix), or null when the file has no version resource.</summary>
    public static string? ProductVersionOf(string path)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
