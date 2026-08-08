namespace VibeRails.Utils;

/// <summary>
/// Compares two project paths for "same project".
///
/// Project paths reach the app from several places — the launch directory, a git root, a stored
/// environment or sandbox row — and those spellings differ in trailing separators and, on
/// Windows, in case. Comparing them raw makes an environment vanish from its own project because
/// one caller said <c>C:\src\app</c> and another said <c>C:\src\app\</c>.
/// </summary>
public static class ProjectPathComparer
{
    /// <summary>
    /// True when both paths denote the same directory. A null or blank path never matches
    /// anything, including another null: "unscoped" is a distinct state from "same project",
    /// and callers that treat null as global must say so explicitly.
    /// </summary>
    public static bool Matches(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(Normalize(left), Normalize(right), PathComparison);
    }

    /// <summary>
    /// How path text compares on this host. Windows and the default macOS filesystem are
    /// case-insensitive, so <c>C:\Src\App</c> and <c>c:\src\app</c> are one project there. On
    /// Linux they are two different directories, and folding case would let one project read or
    /// reuse another's environments and workspaces.
    ///
    /// Filesystem case sensitivity is really a per-volume property (a case-sensitive volume can
    /// be mounted on macOS, and Windows can enable it per directory), so this is a
    /// platform-level approximation. It errs toward the platform default, which is what the
    /// paths the app stores actually come from.
    /// </summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    /// True when an environment scoped to <paramref name="environmentProjectPath"/> should be
    /// visible in <paramref name="currentProjectPath"/>. A null scope means the environment
    /// predates project scoping and stays visible everywhere — the no-backfill guarantee.
    /// </summary>
    public static bool IsVisibleIn(string? environmentProjectPath, string? currentProjectPath)
        => string.IsNullOrWhiteSpace(environmentProjectPath)
            || Matches(environmentProjectPath, currentProjectPath);

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or System.Security.SecurityException)
        {
            // GetFullPath throws on malformed input (bad characters, over-long paths). A stored
            // path that cannot be normalised still compares fine against an identical spelling.
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
