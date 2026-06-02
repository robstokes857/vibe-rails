using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

public class MacTerminalCommandBuilderTests
{
    [Fact]
    public void BuildZshLaunchCommand_ExecsInteractiveLoginZsh()
    {
        const string commandLine = "'/Applications/Vibe Rails/vb' '--env' 'my env'";
        var expectedScript = commandLine + "; exec '/bin/zsh' -l";

        var command = MacTerminalCommandBuilder.BuildZshLaunchCommand(commandLine);

        Assert.Equal("exec '/bin/zsh' -lic " + QuotePosix(expectedScript), command);
    }

    [Fact]
    public void BuildOpenDirectoryCommand_QuotesDirectoryAndExecsZsh()
    {
        var command = MacTerminalCommandBuilder.BuildOpenDirectoryCommand("/Users/rob's/project");

        Assert.Equal("cd '/Users/rob'\\''s/project'; exec '/bin/zsh' -l", command);
    }

    [Fact]
    public void BuildStartInfo_PassesAppleScriptAsArgumentListEntry()
    {
        var startInfo = MacTerminalCommandBuilder.BuildStartInfo("echo \"hello\"");

        Assert.Equal("osascript", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal("-e", startInfo.ArgumentList[0]);
        Assert.Equal(
            "tell application \"Terminal\" to do script \"echo \\\"hello\\\"\"",
            startInfo.ArgumentList[1]);
    }

    private static string QuotePosix(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";
}
