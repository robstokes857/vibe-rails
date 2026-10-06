namespace VibeRails.Utils;

internal static class ShellDefaults
{
    public const string WindowsPtyShell = "pwsh.exe";
    public const string WindowsCommandShell = "pwsh";
    public const string MacOSShell = "/bin/zsh";
    public const string LinuxShell = "bash";

    /// <summary>Finds PowerShell without letting ShellExecute search a request-selected directory.</summary>
    public static string ResolveWindowsCommandShellPath() =>
        ResolveWindowsCommandShellPath(Environment.GetEnvironmentVariable("PATH") ?? "");

    internal static string ResolveWindowsCommandShellPath(string searchPath)
    {
        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var absoluteDirectory = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(absoluteDirectory)) continue;
            var candidate = Path.Combine(absoluteDirectory, "pwsh.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new InvalidOperationException("PowerShell Core (pwsh.exe) was not found in an absolute PATH directory.");
    }

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
