using System.Diagnostics;

namespace VibeRails.Utils;

internal static class MacTerminalCommandBuilder
{
    public static ProcessStartInfo BuildStartInfo(string shellCommand)
    {
        var appleScript = BuildAppleScript(shellCommand);
        var startInfo = new ProcessStartInfo
        {
            FileName = "osascript",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(appleScript);
        return startInfo;
    }

    /// <param name="keepShellOpen">
    /// True leaves an interactive zsh behind once the launched command exits, so the window stays
    /// usable. Job launches pass false: the window exists to show one run, and lingering shells
    /// would accumulate one per run.
    /// </param>
    public static string BuildZshLaunchCommand(string commandLine, bool keepShellOpen = true)
    {
        var zshScript = keepShellOpen
            ? commandLine + "; exec " + QuotePosixSingleQuoted(ShellDefaults.MacOSShell) + " -l"
            : commandLine;
        return "exec "
            + QuotePosixSingleQuoted(ShellDefaults.MacOSShell)
            + " -lic "
            + QuotePosixSingleQuoted(zshScript);
    }

    public static string BuildOpenDirectoryCommand(string directory) =>
        "cd "
        + QuotePosixSingleQuoted(directory)
        + "; exec "
        + QuotePosixSingleQuoted(ShellDefaults.MacOSShell)
        + " -l";

    internal static string BuildAppleScript(string shellCommand) =>
        $"tell application \"Terminal\" to do script \"{EscapeForAppleScriptDoubleQuoted(shellCommand)}\"";

    internal static string QuotePosixSingleQuoted(string value) =>
        ShellArgSanitizer.QuotePosixSingleQuoted(value);

    private static string EscapeForAppleScriptDoubleQuoted(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
