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

    [Fact]
    public void Parse_EnvId_IsReadAlongsideTheName()
    {
        // The name is still passed for display; the id is what the launch resolves.
        var parsed = ArgumentParser.Parse(["--env", "nightly", "--env-id", "42", "--workdir", @"C:\repo"]);

        Assert.Equal(42, parsed.EnvId);
        Assert.Equal("nightly", parsed.LMBootstrapCli);
        Assert.Equal(@"C:\repo", parsed.WorkDir);
    }

    [Theory]
    [InlineData("abc")]   // not a number
    [InlineData("0")]     // not a real row id
    [InlineData("-3")]
    public void Parse_UnusableEnvId_FallsBackToNameResolution(string value)
    {
        var parsed = ArgumentParser.Parse(["--env", "nightly", "--env-id", value]);

        Assert.Null(parsed.EnvId);
        Assert.Equal("nightly", parsed.LMBootstrapCli);
    }

    [Fact]
    public void Parse_EnvIdAfterTheSeparator_StaysWithTheCliPassthrough()
    {
        // Everything past `--` belongs to the LLM CLI. A `--env-id` there is the CLI's own flag,
        // and treating it as vb's would silently redirect the launch to another environment.
        var parsed = ArgumentParser.Parse(["--env", "nightly", "--", "--env-id", "42"]);

        Assert.Null(parsed.EnvId);
        Assert.Equal(["--env-id", "42"], parsed.ExtraArgs);
    }

    [Fact]
    public void Parse_TrailingEnvId_WithNoValue_IsIgnored()
    {
        var parsed = ArgumentParser.Parse(["--env", "nightly", "--env-id"]);

        Assert.Null(parsed.EnvId);
    }
}
