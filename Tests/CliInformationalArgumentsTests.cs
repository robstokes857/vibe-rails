using VibeRails;
using Xunit;

namespace Tests;

public sealed class CliInformationalArgumentsTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--version")]
    [InlineData("-v")]
    public void StandaloneInformation_IsHandledWithoutServices(string argument)
        => Assert.True(CliLoop.TryHandleStandaloneInformation([argument]));

    [Theory]
    [InlineData("mcp", "--help")]
    [InlineData("--env", "--version")]
    [InlineData("--workdir", "--help")]
    public void ProcessModes_KeepTheirExistingArgumentHandling(string first, string second)
        => Assert.False(CliLoop.TryHandleStandaloneInformation([first, second]));

    [Fact]
    public void DashboardLaunch_IsNotConsumed()
    {
        Assert.False(CliLoop.TryHandleStandaloneInformation([]));
        Assert.False(CliLoop.TryHandleStandaloneInformation(["--web"]));
    }
}
