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
        Assert.Contains("Automated Jobs are not queued from preview checks.", text);
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
        Assert.Contains("auto-closes in 2 minutes", output.ToString());
    }

    private sealed class NeverCompletingTextReader : TextReader
    {
        private readonly TaskCompletionSource<string?> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<string?> ReadLineAsync() => _completion.Task;
    }
}
