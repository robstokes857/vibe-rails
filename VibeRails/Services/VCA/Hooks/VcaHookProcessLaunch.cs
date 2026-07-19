using System.Text;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>
/// Shared helpers for re-launching the VibeRails executable as a child process and for
/// deciding whether the current console can host an interactive prompt.
/// </summary>
/// <remarks>
/// Centralized so the hook host (<see cref="VcaHookProcessHost"/>), the popup respawn
/// (<see cref="VcaHookConsoleRespawn"/>), and the acknowledgment relaunch
/// (<see cref="VcaHookRunner"/>) resolve the dotnet host and quote arguments identically.
/// </remarks>
internal static class VcaHookProcessLaunch
{
    /// <summary>
    /// True when a human can both see prompts and type answers in the current console.
    /// </summary>
    public static bool CanPromptInCurrentConsole() =>
        Environment.UserInteractive &&
        !Console.IsInputRedirected &&
        !Console.IsOutputRedirected;

    /// <summary>
    /// Whether the running process is the shared <c>dotnet</c> host (a framework-dependent
    /// launch) rather than a native apphost. Handles both <c>dotnet</c> and <c>dotnet.exe</c>.
    /// </summary>
    public static bool IsDotnetHost(string processPath) =>
        Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Prepends the managed entry assembly as the first argument when running under the
    /// dotnet host, so a re-launched child runs the same application. Returns
    /// <paramref name="args"/> unchanged for native apphost launches.
    /// </summary>
    public static IReadOnlyList<string> WithManagedEntry(string processPath, IReadOnlyList<string> args)
    {
        var commandLineArgs = Environment.GetCommandLineArgs();
        if (IsDotnetHost(processPath) &&
            commandLineArgs.Length > 0 &&
            commandLineArgs[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var withEntry = new List<string>(args.Count + 1) { commandLineArgs[0] };
            withEntry.AddRange(args);
            return withEntry;
        }

        return args;
    }

    /// <summary>
    /// Quotes a single argument so the Win32 command-line parser (<c>CommandLineToArgvW</c>)
    /// round-trips it back to the original value, following the documented
    /// "Everyone quotes command-line arguments the wrong way" recipe.
    /// </summary>
    public static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var needsQuotes = value.Contains(' ') || value.Contains('\t') || value.Contains('"');
        if (!needsQuotes)
        {
            return value;
        }

        var sb = new StringBuilder();
        sb.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var backslashes = 0;
            while (i < value.Length && value[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == value.Length)
            {
                sb.Append('\\', backslashes * 2);
            }
            else if (value[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(value[i]);
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
