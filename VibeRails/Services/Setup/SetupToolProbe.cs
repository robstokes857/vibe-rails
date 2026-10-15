using System.Diagnostics;

namespace VibeRails.Services.Setup;

/// <summary>What the Setup page says about one tool.</summary>
public enum SetupToolState
{
    /// <summary>On this process's PATH, so new VibeRails terminals can launch it.</summary>
    Ready,
    /// <summary>Installed, but not on the PATH this VibeRails process started with.</summary>
    RestartNeeded,
    Missing
}

/// <summary>One probe result. <see cref="Version"/> is read from file metadata, never by running the tool.</summary>
public sealed record SetupToolProbeResult(string Id, SetupToolState State, string? Path, string? Version);

/// <summary>
/// Where the probe looks, split out so tests can describe a machine without touching it.
/// <see cref="AppleShimDirectory"/> (macOS only) holds Apple's developer-tool shims, such as
/// <c>/usr/bin/git</c>, which work only once one of <see cref="AppleDeveloperDirectories"/> has the
/// real tool; until then running one opens the Command Line Tools installer.
/// </summary>
public sealed record SetupProbeEnvironment(
    bool IsWindows,
    IReadOnlyList<string> LaunchPath,
    IReadOnlyList<string> CurrentPath,
    IReadOnlyList<string> ExtraDirectories,
    Func<string, bool> FileExists,
    Func<string, string> ExpandPath,
    Func<string, string?> ReadVersion,
    string? AppleShimDirectory = null,
    IReadOnlyList<string>? AppleDeveloperDirectories = null)
{
    /// <summary>
    /// This machine. <see cref="LaunchPath"/> is the PATH new terminals inherit from this process.
    /// <see cref="CurrentPath"/> adds the user and machine PATH as Windows stores them now, which
    /// is where an installer that ran after VibeRails started puts its directory.
    /// </summary>
    public static SetupProbeEnvironment ForCurrentMachine()
    {
        var isWindows = OperatingSystem.IsWindows();
        var launch = SplitPath(Environment.GetEnvironmentVariable("PATH"));
        var current = isWindows
            ? SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine))
                .Concat(SplitPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User)))
                .Select(Environment.ExpandEnvironmentVariables)
                .ToList()
            : launch;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // The same fallbacks JobExecutableResolver searches, so the page agrees with what an
        // Automation can launch.
        IReadOnlyList<string> extras = isWindows
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
               Path.Combine(home, ".local", "bin")]
            : ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin",
               Path.Combine(home, ".local", "bin"), Path.Combine(home, ".npm-global", "bin")];
        var mac = OperatingSystem.IsMacOS();
        return new SetupProbeEnvironment(isWindows, launch, current, extras, File.Exists,
            path => ExpandHomePath(path, home), path => ReadFileVersion(path, isWindows),
            mac ? "/usr/bin" : null,
            mac ? AppleDeveloperDirectoriesForCurrentMachine() : null);
    }

    // Where xcrun finds the real tools behind the shims: DEVELOPER_DIR, the xcode-select choice
    // (a symlink File.Exists follows), then its two defaults.
    private static List<string> AppleDeveloperDirectoriesForCurrentMachine() =>
    [
        .. Environment.GetEnvironmentVariable("DEVELOPER_DIR") is { Length: > 0 } developerDir ? [developerDir] : Array.Empty<string>(),
        "/var/db/xcode_select_link",
        "/Applications/Xcode.app/Contents/Developer",
        "/Library/Developer/CommandLineTools"
    ];

    /// <summary>Splits a PATH value, dropping blanks, quotes and relative entries.</summary>
    public static List<string> SplitPath(string? value) =>
        (value ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Trim('"'))
            .Where(entry => entry.Length > 0)
            .ToList();

    /// <summary>The user and machine PATH Windows has now, then this process's own entries.</summary>
    public static string CurrentPathValue()
    {
        var environment = ForCurrentMachine();
        var comparer = environment.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return string.Join(System.IO.Path.PathSeparator,
            environment.CurrentPath.Concat(environment.LaunchPath).Distinct(comparer));
    }

    private static string ExpandHomePath(string path, string home) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? System.IO.Path.Combine(home, path[2..])
            : Environment.ExpandEnvironmentVariables(path);

    private static string? ReadFileVersion(string path, bool isWindows)
    {
        if (!isWindows || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            // "7.5.2 SHA: 1a2b..." and "2.47.1.windows.1" both start with the part worth showing.
            var version = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Trim();
            return string.IsNullOrEmpty(version) ? null : version.Split(' ', 2)[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Finds the tools in <see cref="SetupToolCatalog"/> with file-existence checks only. It never
/// starts a process, so a full pass costs a few thousand stat calls.
/// </summary>
public static class SetupToolProbe
{
    public static List<SetupToolProbeResult> Probe(IEnumerable<SetupTool> tools, SetupProbeEnvironment environment) =>
        tools.Select(tool => Probe(tool, environment)).ToList();

    public static SetupToolProbeResult Probe(SetupTool tool, SetupProbeEnvironment environment)
    {
        var onLaunchPath = Find(tool, environment.LaunchPath, environment);
        if (onLaunchPath is not null)
            return new SetupToolProbeResult(tool.Id, SetupToolState.Ready, onLaunchPath, environment.ReadVersion(onLaunchPath));

        var elsewhere = Find(tool, environment.CurrentPath.Concat(environment.ExtraDirectories), environment)
            ?? tool.KnownPaths.Select(environment.ExpandPath).FirstOrDefault(environment.FileExists);
        return elsewhere is null
            ? new SetupToolProbeResult(tool.Id, SetupToolState.Missing, null, null)
            : new SetupToolProbeResult(tool.Id, SetupToolState.RestartNeeded, elsewhere, environment.ReadVersion(elsewhere));
    }

    private static string? Find(SetupTool tool, IEnumerable<string> directories, SetupProbeEnvironment environment)
    {
        foreach (var directory in directories)
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            var windowsApps = environment.IsWindows && IsWindowsAppsDirectory(directory);
            var appleShims = environment.AppleShimDirectory is { } shimDirectory && SameDirectory(directory, shimDirectory);
            foreach (var command in tool.Commands)
            {
                if (windowsApps && tool.WindowsAppsStubs?.Contains(command, StringComparer.OrdinalIgnoreCase) == true)
                    continue;
                if (appleShims && tool.AppleDeveloperShims?.Contains(command, StringComparer.Ordinal) == true
                    && !HasAppleDeveloperTool(command, environment))
                    continue;
                foreach (var name in CandidateNames(command, environment.IsWindows))
                {
                    string candidate;
                    try { candidate = Path.Combine(directory, name); }
                    catch (ArgumentException) { continue; }
                    if (environment.FileExists(candidate)) return candidate;
                }
            }
        }
        return null;
    }

    private static bool HasAppleDeveloperTool(string command, SetupProbeEnvironment environment) =>
        environment.AppleDeveloperDirectories?.Any(directory =>
            environment.FileExists(Path.Combine(directory, "usr", "bin", command))) == true;

    private static bool SameDirectory(string left, string right) =>
        string.Equals(left.TrimEnd('/', '\\'), right.TrimEnd('/', '\\'), StringComparison.Ordinal);

    // Detection, unlike launching, counts npm's .cmd shim: a shell finds it just the same.
    private static IEnumerable<string> CandidateNames(string command, bool isWindows) =>
        isWindows ? [$"{command}.exe", $"{command}.cmd", $"{command}.ps1"] : [command];

    private static bool IsWindowsAppsDirectory(string directory) =>
        directory.Replace('/', '\\').TrimEnd('\\').EndsWith(@"\Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase);
}
