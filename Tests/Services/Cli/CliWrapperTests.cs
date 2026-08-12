using VibeRails.Services.Cli;
using Xunit;

namespace Tests.Services.Cli;

/// <summary>
/// Real (very short) child processes. RunInNewTerminalAsync is deliberately not exercised here —
/// it opens a visible window and is verified by hand; this covers the piped path every other
/// caller will use.
/// </summary>
public class CliWrapperTests
{
    private static readonly ICliWrapper Cli = new CliWrapper();

    private static string WorkDir => Path.GetTempPath();

    /// <summary>A shell invocation that runs one inline command, per platform.</summary>
    private static CliRequest Shell(string windowsCommand, string posixCommand, TimeSpan? timeout = null) =>
        OperatingSystem.IsWindows()
            ? new CliRequest("cmd.exe", ["/c", windowsCommand], WorkDir, Timeout: timeout)
            : new CliRequest("/bin/sh", ["-c", posixCommand], WorkDir, Timeout: timeout);

    [Fact]
    public async Task RunAsync_ReportsTheChildsExitCode()
    {
        var result = await Cli.RunAsync(
            Shell("exit 3", "exit 3"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.IsSuccess);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Equal("exited with code 3", result.DescribeFailure());
    }

    [Fact]
    public async Task RunAsync_SuccessIsExitZeroAndNothingElse()
    {
        var result = await Cli.RunAsync(
            Shell("echo hello", "echo hello"),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains("hello", result.StandardOutput);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task RunAsync_CapturesStdoutAndStderrSeparatelyAndTagsLiveLines()
    {
        var lines = new List<CliOutputLine>();
        var result = await Cli.RunAsync(
            Shell("echo out&& echo err 1>&2", "echo out; echo err 1>&2"),
            line =>
            {
                lock (lines) lines.Add(line);
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains("out", result.StandardOutput);
        Assert.Contains("err", result.StandardError);

        // Interleaved but tagged, which is what the SSE test console renders from.
        Assert.Contains(lines, line => !line.IsError && line.Text.Contains("out"));
        Assert.Contains(lines, line => line.IsError && line.Text.Contains("err"));
    }

    [Fact]
    public async Task RunAsync_LineCallbacksAreSerializedAndStamped()
    {
        var concurrent = 0;
        var maxConcurrent = 0;
        var count = 0;

        await Cli.RunAsync(
            Shell("echo a&& echo b&& echo c 1>&2", "echo a; echo b; echo c 1>&2"),
            async line =>
            {
                var now = Interlocked.Increment(ref concurrent);
                // Not Interlocked: the point of the assertion is that only one callback is ever in
                // flight, so a plain read/write here is safe if the wrapper's gate works.
                if (now > maxConcurrent) maxConcurrent = now;
                count++;
                Assert.True(line.Elapsed >= TimeSpan.Zero);
                await Task.Yield();
                Interlocked.Decrement(ref concurrent);
            },
            TestContext.Current.CancellationToken);

        Assert.True(count >= 3, $"expected at least 3 lines, saw {count}");
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task RunAsync_TimeoutKillsTheChildAndIsReportedAsATimeout()
    {
        var result = await Cli.RunAsync(
            Shell("ping -n 30 127.0.0.1 >NUL", "sleep 30", TimeSpan.FromMilliseconds(400)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.IsSuccess);
        Assert.Equal("timed out", result.DescribeFailure());
        // Killed, not waited out: nowhere near the 30 s the command asked for.
        Assert.True(result.Duration < TimeSpan.FromSeconds(20), $"took {result.Duration}");
    }

    [Fact]
    public async Task RunAsync_CancellationIsReportedAsCancelledNotTimedOut()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        var result = await Cli.RunAsync(
            Shell("ping -n 30 127.0.0.1 >NUL", "sleep 30"),
            cancellationToken: cts.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.Equal("was cancelled", result.DescribeFailure());
    }

    [Fact]
    public async Task RunAsync_RunsInTheRequestedWorkingDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("viberails-cli-test");
        try
        {
            var request = OperatingSystem.IsWindows()
                ? new CliRequest("cmd.exe", ["/c", "cd"], directory.FullName)
                : new CliRequest("/bin/sh", ["-c", "pwd"], directory.FullName);

            var result = await Cli.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.IsSuccess);
            // macOS resolves TMPDIR through /private, so compare on the leaf we created.
            Assert.Contains(directory.Name, result.StandardOutput);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_MissingWorkingDirectory_FailsWithoutStartingAProcess()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"viberails-missing-{Guid.NewGuid():N}");

        var result = await Cli.RunAsync(
            new CliRequest("cmd.exe", ["/c", "echo hi"], missing),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TerminalScriptBuilder.WorkingDirectoryUnavailableExitCode, result.ExitCode);
        Assert.False(result.IsSuccess);
        Assert.Contains("Working directory does not exist", result.StandardError);
    }

    [Fact]
    public async Task RunAsync_PassesEnvironmentVariablesToTheChild()
    {
        var request = OperatingSystem.IsWindows()
            ? new CliRequest("cmd.exe", ["/c", "echo %VIBERAILS_TEST_VAR%"], WorkDir,
                new Dictionary<string, string?> { ["VIBERAILS_TEST_VAR"] = "steps-work" })
            : new CliRequest("/bin/sh", ["-c", "echo $VIBERAILS_TEST_VAR"], WorkDir,
                new Dictionary<string, string?> { ["VIBERAILS_TEST_VAR"] = "steps-work" });

        var result = await Cli.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Contains("steps-work", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_PipesStandardInput()
    {
        var request = OperatingSystem.IsWindows()
            ? new CliRequest("findstr.exe", ["needle"], WorkDir, StandardInput: "hay\nneedle\nhay\n")
            : new CliRequest("/bin/sh", ["-c", "grep needle"], WorkDir, StandardInput: "hay\nneedle\nhay\n");

        var result = await Cli.RunAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("needle", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_UnknownExecutable_FailsInsteadOfThrowing()
    {
        // A step naming a program that is not installed has to render as a failed run in the test
        // console, not blow up the request.
        var result = await Cli.RunAsync(
            new CliRequest($"viberails-nope-{Guid.NewGuid():N}", [], WorkDir),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(TerminalScriptBuilder.LaunchFailedExitCode, result.ExitCode);
        Assert.Contains("Could not start", result.StandardError);
    }

    [Fact]
    public void DescribeScript_TakesTheFirstMeaningfulLine()
    {
        Assert.Equal("npm install", CliWrapper.DescribeScript("\n\n  npm install  \ngit status"));
        Assert.Equal("(empty command)", CliWrapper.DescribeScript("   \n  \n"));
    }
}
