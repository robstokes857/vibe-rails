using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

/// <summary>
/// Program.cs exits with code 2 before building the web host when these argv shapes appear
/// (VIBE-292). Base CLI bootstraps stay live (VIBE-296); custom environments and Jobs do not.
/// </summary>
public sealed class RetiredLaunchArgumentsTests
{
    [Theory]
    [InlineData("--env", "claude", "--workdir", "C:\\repo")]
    [InlineData("--env", "CODEX", "--workdir", "/home/me/repo")]
    [InlineData("--env", "glm-5.2", "--workdir", "C:\\repo")]
    [InlineData("--env", "grok", "--workdir", "C:\\repo", "--", "--env-id", "3")]
    [InlineData("--env", "claude", "--workdir", "C:\\repo", "--", "--job-run", "x", "--env", "custom")]
    [InlineData("--vs-code-v1", "--parent-pid", "42")]
    [InlineData("--web")]
    public void BaseCliAndOrdinaryModesAreNotRetired(params string[] args) =>
        Assert.False(RetiredLaunchArguments.IsRetired(args));

    [Theory]
    [InlineData("--env", "my-custom-env", "--workdir", "C:\\repo")]
    [InlineData("--env", "--workdir", "C:\\repo")]
    [InlineData("--env")]
    [InlineData("--env=claude")]
    [InlineData("--env", "claude", "--env-id", "7")]
    [InlineData("--env-id=7")]
    [InlineData("--workdir", "C:\\repo", "--job-run", "run-1")]
    [InlineData("--JOB-RUN=run-1")]
    public void CustomEnvironmentsAndJobsAreRetired(params string[] args) =>
        Assert.True(RetiredLaunchArguments.IsRetired(args));
}
