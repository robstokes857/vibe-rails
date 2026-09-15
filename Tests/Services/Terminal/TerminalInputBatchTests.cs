using System.Text;
using Moq;
using Pty.Net;
using VibeRails.Services.Terminal;
using Xunit;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

public sealed class TerminalInputBatchTests
{
    [Theory]
    [InlineData("raw", "outside")]
    [InlineData("routed", "outside")]
    [InlineData("escape", "\u001b")]
    public async Task OtherInputSourcesCannotSplitAnAutomatedSequence(string source, string suffix)
    {
        using var output = new MemoryStream();
        await using var terminal = new TerminalPty(new InputPty(output), cols: 80, rows: 24);
        var state = new Mock<ITerminalStateService>();
        var recorded = new List<string>();
        state.Setup(s => s.RecordInput("session", It.IsAny<string>(), It.IsAny<TerminalIoSource>()))
            .Callback<string, string, TerminalIoSource>((_, text, _) => recorded.Add(text));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ct = TestContext.Current.CancellationToken;
        var batch = TerminalIoRouter.RouteInputBatchAsync(state.Object, terminal, "session", TerminalIoSource.AgentTool,
            async (send, escape, token) =>
            {
                await escape(token);
                entered.TrySetResult();
                await resume.Task.WaitAsync(token);
                await escape(token);
                await send("\u001b[200~task\u001b[201~", token);
                await send("\r", token);
            }, ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var competing = source switch
        {
            "raw" => terminal.WriteAsync("outside", ct),
            "escape" => TerminalIoRouter.RouteEscapeKeyAsync(state.Object, terminal, "session", TerminalIoSource.LocalWebUi, ct),
            _ => TerminalIoRouter.RouteInputAsync(state.Object, terminal, "session", "outside", TerminalIoSource.RemoteWebUi, ct)
        };
        try
        {
            Assert.False(competing.IsCompleted);
            Assert.Equal("\u001b", Encoding.UTF8.GetString(output.ToArray()));
            Assert.Equal(["\u001b"], recorded);
        }
        finally { resume.TrySetResult(); }
        await Task.WhenAll(batch, competing);
        Assert.Equal("\u001b\u001b\u001b[200~task\u001b[201~\r" + suffix, Encoding.UTF8.GetString(output.ToArray()));
        if (source != "raw") Assert.Equal(suffix, recorded[^1]);
    }

    [Fact]
    public async Task FailedBatchReleasesTheGateAndInvalidatesItsWriter()
    {
        using var output = new MemoryStream();
        await using var terminal = new TerminalPty(new InputPty(output), cols: 80, rows: 24);
        TerminalPty.InputBatch? escapedWriter = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => terminal.RunInputBatchAsync((writer, _) =>
        {
            escapedWriter = writer;
            throw new InvalidOperationException("failed sequence");
        }, TestContext.Current.CancellationToken));
        await terminal.WriteAsync("still writable", TestContext.Current.CancellationToken);
        Assert.Equal("still writable", Encoding.UTF8.GetString(output.ToArray()));
        Assert.NotNull(escapedWriter);
        Assert.Throws<ObjectDisposedException>(() => { _ = escapedWriter.WriteEscapeKeyAsync(TestContext.Current.CancellationToken); });
    }

    private sealed class InputPty(Stream writer) : IPtyConnection
    {
        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
        public Stream ReaderStream => Stream.Null;
        public Stream WriterStream => writer;
        public int Pid => 0;
        public int ExitCode => 0;
        public bool WaitForExit(int milliseconds) => true;
        public void Kill() { }
        public void KillProcessTree() { }
        public void Resize(int cols, int rows) { }
        public void Dispose() { }
    }
}
