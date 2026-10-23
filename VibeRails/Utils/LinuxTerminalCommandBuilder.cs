using System.Diagnostics;

namespace VibeRails.Utils;

/// <summary>
/// Opens a Linux terminal emulator in a directory. The directory travels as one argv element
/// (or as the child's working directory), never inside a command string, and nothing goes
/// through shell execution.
/// </summary>
internal static class LinuxTerminalCommandBuilder
{
    /// <summary>Emulators tried in order until one starts.</summary>
    public static readonly IReadOnlyList<string> Terminals =
        ["gnome-terminal", "konsole", "xfce4-terminal", "xterm"];

    public static ProcessStartInfo BuildOpenDirectoryStartInfo(string terminal, string directory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = terminal,
            WorkingDirectory = directory,
            UseShellExecute = false,
        };

        // gnome-terminal hands the window to its server process, which ignores the launcher's
        // working directory, so it needs the explicit flag. The others start in the child cwd.
        if (terminal == "gnome-terminal")
        {
            startInfo.ArgumentList.Add($"--working-directory={directory}");
        }

        return startInfo;
    }
}
