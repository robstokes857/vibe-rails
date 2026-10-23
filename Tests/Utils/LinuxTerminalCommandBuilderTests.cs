using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

/// <summary>
/// The sandbox "open shell" launch on Linux. The directory used to be interpolated into an
/// <c>Arguments</c> string with shell execution on (VIBE-194, scan finding L-24).
/// </summary>
public class LinuxTerminalCommandBuilderTests
{
    private const string HostileDirectory = "/home/rob/sandboxes/a\" --command=\"touch /tmp/pwned";

    [Fact]
    public void GnomeTerminalReceivesTheDirectoryAsOneArgvElement()
    {
        var startInfo = LinuxTerminalCommandBuilder.BuildOpenDirectoryStartInfo("gnome-terminal", HostileDirectory);

        Assert.Equal("gnome-terminal", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(["--working-directory=" + HostileDirectory], startInfo.ArgumentList);
        Assert.Equal(HostileDirectory, startInfo.WorkingDirectory);
    }

    [Theory]
    [InlineData("konsole")]
    [InlineData("xfce4-terminal")]
    [InlineData("xterm")]
    public void OtherEmulatorsStartInTheDirectoryWithNoArguments(string terminal)
    {
        var startInfo = LinuxTerminalCommandBuilder.BuildOpenDirectoryStartInfo(terminal, HostileDirectory);

        Assert.Equal(terminal, startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Empty(startInfo.Arguments);
        Assert.Empty(startInfo.ArgumentList);
        Assert.Equal(HostileDirectory, startInfo.WorkingDirectory);
    }
}
