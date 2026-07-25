using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

public class VcaHookProcessHostTests
{
    [Fact]
    public async Task RunAsync_PreviewMode_RendersHookProgressOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await VcaHookProcessHost.RunAsync(
            ["--vca-hook", "preview", "--demo-duration-ms", "1"],
            output,
            error,
            cancellationToken: TestContext.Current.CancellationToken);

        var text = output.ToString();
        var firstLine = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).First();

        Assert.Equal(0, exitCode);
        Assert.Equal("VibeRails · Git Guard — Preview", firstLine);
        Assert.Contains("[1/3]", text);
        Assert.Contains("PASS: VCA hook preview completed.", text);
        Assert.Contains("MintLint code health", text);
        // The pre-commit VCA trigger is gone; the step is a no-op that still reports itself so the
        // hook's [n/3] progress stays accurate.
        Assert.Contains("Automated workflows", text);
        Assert.DoesNotContain("Queued", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[pass] Commit allowed", text);
    }

    [Fact]
    public async Task PauseForEnterAsync_ReturnsWhenTimeoutElapses()
    {
        using var output = new StringWriter();
        using var input = new NeverCompletingTextReader();

        var pauseTask = VcaHookProcessHost.PauseForEnterAsync(
            output,
            input,
            exitCode: 0,
            timeout: TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken);
        var completed = await Task.WhenAny(
            pauseTask,
            Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        Assert.Same(pauseTask, completed);
        await pauseTask;
        // The prompt has to describe the timeout it was actually given. This previously read
        // "auto-closes in 5 seconds" no matter what was passed, so the window's promise and its
        // behaviour could drift apart silently — which is exactly what happened.
        Assert.Contains("auto-closes in 1 second", output.ToString());
    }

    [Fact]
    public void ConsolePauseTimeout_IsLongEnoughToReadStopViolations()
    {
        // The popup's whole purpose is letting the user read why a commit was blocked. Pinned
        // because it was once cut to 5 seconds, which is not enough time to read a violation list.
        Assert.True(
            VcaHookProcessHost.ConsolePauseTimeout >= TimeSpan.FromSeconds(15),
            $"Pause timeout is {VcaHookProcessHost.ConsolePauseTimeout.TotalSeconds:0}s.");

        Assert.Equal(
            "auto-closes in 30 seconds",
            VcaHookProcessHost.DescribeAutoClose(VcaHookProcessHost.ConsolePauseTimeout));
    }

    [Fact]
    public async Task PauseForEnterAsync_AnsiStyle_ShowsCountdownAndClosesOnEnter()
    {
        using var output = new StringWriter();
        using var input = new StringReader(Environment.NewLine);

        await VcaHookProcessHost.PauseForEnterAsync(
            output,
            input,
            exitCode: 0,
            timeout: TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken,
            VcaConsoleStyle.Ansi);

        var text = AnsiText.Strip(output.ToString());
        Assert.Contains("VCA check complete.", text);
        Assert.Contains("auto-closes in 2:00", text);
        Assert.Contains("\x1b[", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PauseForEnterAsync_AnsiStyle_ReturnsWhenTimeoutElapses()
    {
        using var output = new StringWriter();
        using var input = new NeverCompletingTextReader();

        var pauseTask = VcaHookProcessHost.PauseForEnterAsync(
            output,
            input,
            exitCode: 1,
            timeout: TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken,
            VcaConsoleStyle.Ansi);
        var completed = await Task.WhenAny(
            pauseTask,
            Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        Assert.Same(pauseTask, completed);
        await pauseTask;
        Assert.Contains("VCA check blocked the commit (exit code 1).", output.ToString());
    }

    private sealed class NeverCompletingTextReader : TextReader
    {
        private readonly TaskCompletionSource<string?> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<string?> ReadLineAsync() => _completion.Task;
    }
}
