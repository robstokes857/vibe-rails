namespace VibeRails.Utils;

internal static class ShellDefaults
{
    public const string WindowsPtyShell = "pwsh.exe";
    public const string WindowsCommandShell = "pwsh";
    public const string MacOSShell = "/bin/zsh";
    public const string LinuxShell = "bash";

    public static string GetDefaultPtyShellPath() =>
        GetDefaultPtyShellPath(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());

    internal static string GetDefaultPtyShellPath(bool isWindows, bool isMacOS)
    {
        if (isWindows)
            return WindowsPtyShell;

        return isMacOS ? MacOSShell : LinuxShell;
    }

    public static string GetUnixCommandShellPath() =>
        GetUnixCommandShellPath(OperatingSystem.IsMacOS());

    internal static string GetUnixCommandShellPath(bool isMacOS) =>
        isMacOS ? MacOSShell : LinuxShell;
}
