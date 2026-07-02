using Microsoft.Extensions.Logging.Abstractions;
using VibeRails.Services.Mcp.HostShell;
using Xunit;

namespace Tests.Services.Mcp;

public sealed class HostShellCommandServiceTests
{
    [Fact]
    public async Task RunAsync_ExecutesCommandAndCapturesOutput()
    {
        await using var service = new HostShellCommandService(NullLogger<HostShellCommandService>.Instance);
        var command = OperatingSystem.IsWindows()
            ? "Write-Output 'vibe-shell-ok'"
            : "printf 'vibe-shell-ok\\n'";

        var result = await service.RunAsync(new HostShellCommandRequest(
            Command: command,
            TimeoutSeconds: 10,
            WaitSeconds: 20),
            TestContext.Current.CancellationToken);

        Assert.Equal(HostShellCommandStatus.Completed, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("vibe-shell-ok", result.Stdout);
    }

    [Fact]
    public async Task RunAsync_CapturesStderrBeforeReturningCompletedResult()
    {
        await using var service = new HostShellCommandService(NullLogger<HostShellCommandService>.Instance);
        var command = OperatingSystem.IsWindows()
            ? "[Console]::Error.WriteLine('vibe-shell-err')"
            : "printf 'vibe-shell-err\\n' >&2";

        var result = await service.RunAsync(new HostShellCommandRequest(
            Command: command,
            TimeoutSeconds: 10,
            WaitSeconds: 20),
            TestContext.Current.CancellationToken);

        Assert.Equal(HostShellCommandStatus.Completed, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("vibe-shell-err", result.Stderr);
    }

    [Fact]
    public async Task RunAsync_PreservesPowerShellOutputThatLooksLikePrompt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var service = new HostShellCommandService(NullLogger<HostShellCommandService>.Instance);

        var result = await service.RunAsync(new HostShellCommandRequest(
            Command: "Write-Output 'PS repo> this is real output'",
            TimeoutSeconds: 10,
            WaitSeconds: 20),
            TestContext.Current.CancellationToken);

        Assert.Equal(HostShellCommandStatus.Completed, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PS repo> this is real output", result.Stdout);
    }
}
