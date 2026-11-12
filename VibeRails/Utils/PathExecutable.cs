using System.Collections.Concurrent;

namespace VibeRails.Utils;

/// <summary>
/// Finds a program by name in the absolute directories on PATH, and nowhere else.
/// </summary>
/// <remarks>
/// Starting a process with a bare name does not search PATH first. Windows' CreateProcess looks in
/// the application directory and then the current directory; .NET's Unix resolver and CliWrap's
/// script probing do the same. Every VibeRails process runs with the opened project as its current
/// directory, so a file named like the program in that project would be started instead
/// (API_SEC.md: "Repository folders never select these programs"). Resolve the name here and start
/// the absolute path. VIBE-310.
/// </remarks>
public static class PathExecutable
{
    private static readonly ConcurrentDictionary<(string Name, bool Scripts), string> Resolved = new();

    /// <summary>
    /// The absolute path of <paramref name="name"/> from PATH. A value that already names a
    /// directory is returned unchanged. A program that is not on PATH resolves to a rooted path
    /// that does not exist, so starting it fails as a missing program does and never falls back to
    /// a lookup in the current directory.
    /// </summary>
    /// <param name="name">A program name such as <c>git</c>.</param>
    /// <param name="allowScripts">
    /// On Windows, also accept <c>.cmd</c> and <c>.bat</c> beside <c>.exe</c>, as CliWrap does for
    /// a name without an extension. Only for callers that start the result through CliWrap.
    /// </param>
    public static string Resolve(string name, bool allowScripts = false)
    {
        if (string.IsNullOrWhiteSpace(name) || HasDirectory(name))
            return name;
        return Resolved.GetOrAdd((name, allowScripts), static key =>
            Find(key.Name, Environment.GetEnvironmentVariable("PATH") ?? "", OperatingSystem.IsWindows(), key.Scripts) ?? Missing(key.Name));
    }

    /// <summary>The first match in the fully qualified entries of <paramref name="searchPath"/>, or null.</summary>
    internal static string? Find(string name, string searchPath, bool isWindows, bool allowScripts = false)
    {
        if (string.IsNullOrWhiteSpace(name) || HasDirectory(name))
            return null;
        string[] candidates = !isWindows || Path.HasExtension(name)
            ? [name]
            : allowScripts ? [name + ".exe", name + ".cmd", name + ".bat"] : [name + ".exe"];

        foreach (var entry in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            // A relative entry ("." or "bin") would resolve against the current directory: the project.
            if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
                continue;
            foreach (var candidate in candidates)
            {
                try
                {
                    var path = Path.GetFullPath(Path.Combine(directory, candidate));
                    if (File.Exists(path) && (isWindows || IsUnixExecutable(path)))
                        return path;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
                {
                    // A malformed or unreadable PATH entry; try the next one.
                }
            }
        }
        return null;
    }

    /// <summary>Where a program that is not on PATH points: rooted, and never present.</summary>
    internal static string Missing(string name) =>
        Path.Combine(AppContext.BaseDirectory, ".not-on-path", name);

    private static bool HasDirectory(string name) =>
        name.Contains('/') || name.Contains('\\') || Path.IsPathRooted(name);

    private static bool IsUnixExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return true;
        const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & anyExecute) != 0;
    }
}
