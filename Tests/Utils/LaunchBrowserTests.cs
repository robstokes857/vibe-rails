using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

/// <summary>
/// The browser is opened with the URL as one value and without naming a program by its bare name
/// (VIBE-310): no cmd.exe command line is built from the URL.
/// </summary>
public sealed class LaunchBrowserTests
{
    private const string Bootstrap = "http://localhost:5000/auth/bootstrap?code=abc_DEF-123&redirectArgs=--web%20%22a%26b%22";

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vscode://file/C:/x")]
    [InlineData("ms-settings:privacy")]
    public void OnlyWebUrlsAreOpened(string value) => Assert.Null(LaunchBrowser.ParseWebUrl(value));

    [Fact]
    public void AMissingSchemeBecomesHttps()
    {
        Assert.Equal("https://viberails.ai/", LaunchBrowser.ParseWebUrl("viberails.ai")!.AbsoluteUri);
        Assert.Equal(Bootstrap, LaunchBrowser.ParseWebUrl(Bootstrap)!.AbsoluteUri);
    }

    [Fact]
    public void WindowsHandsTheEscapedUrlToTheShellWithNoCommandLine()
    {
        var startInfo = LaunchBrowser.CreateStartInfo(LaunchBrowser.ParseWebUrl(Bootstrap)!, isWindows: true, isMacOS: false, isLinux: false)!;

        Assert.Equal(Bootstrap, startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Empty(startInfo.Arguments);
        Assert.Empty(startInfo.ArgumentList);
        Assert.DoesNotContain('"', startInfo.FileName);
        Assert.DoesNotContain(' ', startInfo.FileName);
    }

    [Fact]
    public void UnixPassesTheUrlAsOneArgumentToAnAbsoluteOpener()
    {
        var uri = LaunchBrowser.ParseWebUrl(Bootstrap)!;
        var mac = LaunchBrowser.CreateStartInfo(uri, isWindows: false, isMacOS: true, isLinux: false)!;
        var linux = LaunchBrowser.CreateStartInfo(uri, isWindows: false, isMacOS: false, isLinux: true)!;

        Assert.Equal("/usr/bin/open", mac.FileName);
        Assert.True(Path.IsPathRooted(linux.FileName));
        Assert.StartsWith("xdg-open", Path.GetFileName(linux.FileName), StringComparison.Ordinal);
        foreach (var startInfo in new[] { mac, linux })
        {
            Assert.False(startInfo.UseShellExecute);
            Assert.Empty(startInfo.Arguments);
            Assert.Equal([Bootstrap], startInfo.ArgumentList);
        }
        Assert.Null(LaunchBrowser.CreateStartInfo(uri, isWindows: false, isMacOS: false, isLinux: false));
    }
}
