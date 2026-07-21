namespace VibeRails.Utils;

/// <summary>
/// Applies owner-only Unix permissions to VibeRails data that can contain API keys, terminal
/// transcripts, repository paths, and session state. Windows uses the user-profile ACL instead.
/// </summary>
internal static class PrivateFilePermissions
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
    }

    internal static void EnsureFile(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }
}
