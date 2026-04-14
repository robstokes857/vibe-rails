using VibeRails.Services.UserInOut;
using Xunit;

namespace Tests.Services.BertV2;

public class InputEtlFilterTests
{
    // ── Secret detection ──────────────────────────────────────────

    [Theory]
    [InlineData("sk-ant-api03-FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKE-TESTONLY-000000000000000000000000_NOTREAL-xTEST0xAAA")]
    [InlineData("my key is sk-ant-api03-abcdefghijklmnopqrstu")]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz1234")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijkl")]
    [InlineData("github_pat_ABCDEFGHIJKLMNOPQRSTUVwx")]
    [InlineData("xoxb-123456789012-1234567890123-abcdefghij")]
    [InlineData("export ANTHROPIC_API_KEY=sk-ant-api03-abc123")]
    [InlineData("[Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY','sk-ant-api03-abc')")]
    [InlineData("password=super_secret_value_here")]
    [InlineData("api_key: sk-ant-api03-FAKEFAKEFAKE_NOTAREAL")]
    public void ContainsSecret_DetectsSecrets(string input)
    {
        Assert.True(InputEtlFilter.ContainsSecret(input));
    }

    [Theory]
    [InlineData("can you add search functionality?")]
    [InlineData("fix the login page CSS")]
    [InlineData("@file.ts refactor this module")]
    [InlineData("deploy the application to production")]
    [InlineData("The server crashed with an out of memory error")]
    [InlineData("sk-short")]  // Too short to be a real key
    [InlineData("https://github.com/user/repo")]
    [InlineData("set the background color to blue")]
    public void ContainsSecret_AllowsNormalText(string input)
    {
        Assert.False(InputEtlFilter.ContainsSecret(input));
    }

    // ── Noise detection ───────────────────────────────────────────

    [Theory]
    [InlineData("ls")]
    [InlineData("cd")]
    [InlineData("pwd")]
    [InlineData("clear")]
    [InlineData("exit")]
    [InlineData("q")]
    [InlineData("y")]
    [InlineData("n")]
    [InlineData("..")]
    [InlineData("ls -la")]
    [InlineData("cd /tmp")]
    [InlineData("42")]
    [InlineData("1")]
    [InlineData("LS")]  // case-insensitive
    public void IsNoise_DetectsNoiseCommands(string input)
    {
        Assert.True(InputEtlFilter.IsNoise(input));
    }

    [Theory]
    [InlineData("add search functionality")]
    [InlineData("fix the login bug")]
    [InlineData("@file.ts refactor this")]
    [InlineData("can you help me debug this?")]
    [InlineData("git commit -m 'fix: resolve login issue'")]
    public void IsNoise_AllowsMeaningfulInput(string input)
    {
        Assert.False(InputEtlFilter.IsNoise(input));
    }

    // ── Normalization ─────────────────────────────────────────────

    [Theory]
    [InlineData("> foo bar", "foo bar")]
    [InlineData("$ git status", "git status")]
    [InlineData("% echo hello", "echo hello")]
    [InlineData("# root command", "root command")]
    [InlineData(">>> python expr", "python expr")]
    [InlineData("> > nested prompt", "nested prompt")]
    [InlineData("  spaces  everywhere  ", "spaces everywhere")]
    [InlineData("null\0bytes\0here", "nullbyteshere")]
    [InlineData("cr\r\nlf", "cr lf")]
    public void Normalize_CleansInput(string input, string expected)
    {
        Assert.Equal(expected, InputEtlFilter.Normalize(input));
    }

    // ── Full pipeline ─────────────────────────────────────────────

    [Fact]
    public void Process_SkipsSecrets()
    {
        Assert.Null(InputEtlFilter.Process("sk-ant-api03-FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKE-TESTONLY-000000000000000000000000_NOTREAL-xTEST0xAAA"));
    }

    [Fact]
    public void Process_SkipsNoise()
    {
        Assert.Null(InputEtlFilter.Process("ls"));
        Assert.Null(InputEtlFilter.Process("cd"));
        Assert.Null(InputEtlFilter.Process("42"));
    }

    [Fact]
    public void Process_SkipsEmpty()
    {
        Assert.Null(InputEtlFilter.Process(null));
        Assert.Null(InputEtlFilter.Process(""));
        Assert.Null(InputEtlFilter.Process("   "));
    }

    [Fact]
    public void Process_NormalizesAndReturnsGoodInput()
    {
        Assert.Equal("add search functionality", InputEtlFilter.Process("> add search functionality"));
    }

    [Fact]
    public void Process_PreservesAtFileReferences()
    {
        var result = InputEtlFilter.Process("@VibeRails/wwwroot/js/modules/chat-history-sidebar.js can you add search?");
        Assert.Equal("@VibeRails/wwwroot/js/modules/chat-history-sidebar.js can you add search?", result);
    }

    [Fact]
    public void Process_SkipsEnvironmentVariableWithSecret()
    {
        Assert.Null(InputEtlFilter.Process(
            "[Environment]::SetEnvironmentVariable('ANTHROPIC_API_KEY','sk-ant-api03-FAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKEFAKE-TESTONLY-000000000000000000000000_NOTREAL-xTEST0xAAA','User')"));
    }
}
