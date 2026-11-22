using System.Runtime.InteropServices;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Where the bundled Windows console host lives and whether a new terminal should use it
/// (VIBE-354). The pair <c>conpty.dll</c> + <c>OpenConsole.exe</c> ships in a <c>conpty</c> folder
/// beside the executable; the build copies both from the Microsoft.Windows.Console.ConPTY package.
///
/// <para>
/// Off unless the user turns on <see cref="Settings.ModernConsoleHost"/>. With it off nothing here
/// runs and every terminal uses the ConPTY built into Windows, as before.
/// </para>
/// </summary>
internal static class BundledConsoleHost
{
    internal const string DirectoryName = "conpty";
    internal const string LibraryName = "conpty.dll";
    internal const string HostName = "OpenConsole.exe";

    /// <summary>
    /// Absolute path of the bundled conpty.dll for this install, whether or not it exists. Always
    /// under the executable's own directory: it is loaded by this path and never searched for.
    /// </summary>
    internal static string LibraryPath => Path.Combine(AppContext.BaseDirectory, DirectoryName, LibraryName);

    /// <summary>The package ships an x64 pair, and VibeRails for Windows is x64 only.</summary>
    internal static bool IsSupportedPlatform =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    /// <summary>
    /// Whether this install carries both files. A conpty.dll without OpenConsole.exe beside it is
    /// not usable: it would start the built-in host and report success.
    /// </summary>
    internal static bool IsAvailable(out string? unavailableReason) =>
        IsAvailable(LibraryPath, IsSupportedPlatform, out unavailableReason);

    internal static bool IsAvailable(string libraryPath, bool supportedPlatform, out string? unavailableReason)
    {
        if (!supportedPlatform)
        {
            unavailableReason = "The modern console host is only available on Windows x64.";
            return false;
        }

        var directory = Path.GetDirectoryName(libraryPath) ?? string.Empty;
        if (!File.Exists(libraryPath) || !File.Exists(Path.Combine(directory, HostName)))
        {
            unavailableReason = $"This build does not include {LibraryName} and {HostName}.";
            return false;
        }

        unavailableReason = null;
        return true;
    }

    /// <summary>
    /// The library a new terminal should be hosted with, or null for the built-in host.
    /// </summary>
    /// <param name="nativeConsole">
    /// True for a session that mirrors its PTY into a real console window. Those stay on the
    /// built-in host: the outer console would answer the forwarded queries as well, and each
    /// second answer reaches the program as typed characters.
    /// </param>
    /// <remarks>
    /// The path is returned without checking the files. Pty.Net checks the pair when it spawns
    /// and reports why it fell back, which is the one place that reason is produced and logged.
    /// </remarks>
    internal static string? ResolveForNewTerminal(Settings settings, bool nativeConsole) =>
        ResolveForNewTerminal(settings, nativeConsole, IsSupportedPlatform, LibraryPath);

    internal static string? ResolveForNewTerminal(
        Settings settings, bool nativeConsole, bool supportedPlatform, string libraryPath) =>
        settings.ModernConsoleHost && !nativeConsole && supportedPlatform ? libraryPath : null;
}
