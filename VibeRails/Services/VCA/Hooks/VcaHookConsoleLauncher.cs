using System.Diagnostics;

namespace VibeRails.Services.VCA.Hooks;

public sealed record VcaHookConsoleLaunchResult(bool Success, string Message);

/// <summary>
/// Opens the real Git Guard hook popup on demand, so the pre-commit checks can be run by hand
/// before staging is final — and so the popup a commit will produce can be seen without making
/// a commit to see it.
/// </summary>
/// <remarks>
/// This deliberately takes the same route a GUI Git client's commit takes rather than a private
/// one: the child is launched with <c>--console-window</c> and no console of its own, which sends
/// it straight through <see cref="VcaHookConsoleRespawn"/> to a CREATE_NEW_CONSOLE popup. Running
/// the hook a different way here would mean the thing under review is not the thing that ships.
///
/// The launch is fire-and-forget. Waiting would hold the HTTP request open for the whole run plus
/// the popup's read-time pause, and the popup is the user's window into the result anyway.
/// </remarks>
public static class VcaHookConsoleLauncher
{
    public static VcaHookConsoleLaunchResult LaunchPreCommit(string repositoryPath) =>
        Launch(repositoryPath, "pre-commit");

    internal static VcaHookConsoleLaunchResult Launch(string repositoryPath, string hookKind)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
        {
            return new VcaHookConsoleLaunchResult(false, "The repository path is unavailable.");
        }

        if (!OperatingSystem.IsWindows())
        {
            return new VcaHookConsoleLaunchResult(
                false,
                "Opening a hook console window is only supported on Windows. "
                    + "Run the checks from a terminal instead.");
        }

        if (!Environment.UserInteractive)
        {
            return new VcaHookConsoleLaunchResult(
                false,
                "VibeRails is running without an interactive desktop session, so it cannot open a "
                    + "console window.");
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return new VcaHookConsoleLaunchResult(false, "The VibeRails executable could not be located.");
        }

        var arguments = VcaHookProcessLaunch.WithManagedEntry(
            processPath,
            ["--vca-hook", hookKind, "--workdir", repositoryPath, "--console-window"]);

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            WorkingDirectory = repositoryPath,
            UseShellExecute = false,
            // This process only exists to hand the run to the popup; the popup is the visible one.
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            return process == null
                ? new VcaHookConsoleLaunchResult(false, "The hook console process could not be started.")
                : new VcaHookConsoleLaunchResult(
                    true,
                    "Opened the Git Guard hook console. It closes itself unless something blocks the commit.");
        }
        catch (Exception exception)
        {
            return new VcaHookConsoleLaunchResult(
                false,
                $"The hook console could not be started: {exception.Message}");
        }
    }
}
