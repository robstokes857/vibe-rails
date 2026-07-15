using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

public class ArgumentParserTests
{
    [Fact]
    public void Parse_GitGuard_OpensFocusedBrowserMode()
    {
        var parsed = ArgumentParser.Parse(["--git-guard"]);

        Assert.True(parsed.IsGitGuardMode);
        Assert.True(parsed.OpenBrowser);
        Assert.True(parsed.ShutdownOnBrowserClose);
        Assert.False(parsed.IsLMBootstrap);
        Assert.False(parsed.IsVsCodeMode);
    }

    [Fact]
    public void Parse_DefaultWebMode_DoesNotSelectGitGuard()
    {
        var parsed = ArgumentParser.Parse([]);

        Assert.False(parsed.IsGitGuardMode);
        Assert.True(parsed.OpenBrowser);
    }
}
