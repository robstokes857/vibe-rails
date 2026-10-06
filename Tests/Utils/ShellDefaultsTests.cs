using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

public class ShellDefaultsTests
{
    [Fact]
    public void WindowsShellResolutionSkipsRelativeSearchEntriesAndReturnsAnAbsoluteExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "viberails-shell-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Combine(directory, "pwsh.exe");
            File.WriteAllText(executable, "test fixture, never executed");
            Assert.Equal(executable, ShellDefaults.ResolveWindowsCommandShellPath(
                string.Join(Path.PathSeparator, ".", "relative", $"\"{directory}\"")));
            Assert.Throws<InvalidOperationException>(() => ShellDefaults.ResolveWindowsCommandShellPath(
                string.Join(Path.PathSeparator, ".", "relative", "")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void GetDefaultPtyShellPath_MacOS_UsesZsh()
    {
        Assert.Equal("/bin/zsh", ShellDefaults.GetDefaultPtyShellPath(isWindows: false, isMacOS: true));
    }

    [Fact]
    public void GetDefaultPtyShellPath_Linux_UsesBash()
    {
        Assert.Equal("bash", ShellDefaults.GetDefaultPtyShellPath(isWindows: false, isMacOS: false));
    }

    [Fact]
    public void GetDefaultPtyShellPath_Windows_UsesPwshExe()
    {
        Assert.Equal("pwsh.exe", ShellDefaults.GetDefaultPtyShellPath(isWindows: true, isMacOS: false));
    }

    [Fact]
    public void GetUnixCommandShellPath_MacOS_UsesZsh()
    {
        Assert.Equal("/bin/zsh", ShellDefaults.GetUnixCommandShellPath(isMacOS: true));
    }

    [Fact]
    public void GetUnixCommandShellPath_Linux_UsesBash()
    {
        Assert.Equal("bash", ShellDefaults.GetUnixCommandShellPath(isMacOS: false));
    }
}
