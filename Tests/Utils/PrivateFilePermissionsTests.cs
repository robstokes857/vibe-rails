using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

public sealed class PrivateFilePermissionsTests
{
    [Fact]
    public void EnsureDirectoryAndFile_SetOwnerOnlyModes_OnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"viberails-permissions-{Guid.NewGuid():N}");
        var file = Path.Combine(root, "settings.json");
        try
        {
            PrivateFilePermissions.EnsureDirectory(root);
            File.WriteAllText(file, "{}");
            PrivateFilePermissions.EnsureFile(file);

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(root));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(file));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
