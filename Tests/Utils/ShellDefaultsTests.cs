using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

public class ShellDefaultsTests
{
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
