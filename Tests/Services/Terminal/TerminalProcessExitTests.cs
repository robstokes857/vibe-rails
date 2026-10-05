using System.Collections.Concurrent;
using System.Reflection;
using Pty.Net;
using VibeRails.Services.Terminal;
using Xunit;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

/// <summary>
/// VIBE-45: a session must end when its PTY process exits, even when the output pipe never reaches
/// EOF. ConPTY keeps the pipe open while any client is still attached to the console, so an Automation
/// wrapper shell whose Worker left a compiler server behind kept its finished agent "Running" for hours.
/// </summary>
public sealed class TerminalProcessExitTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task AgentCompletionDoesNotRelabelAnAlreadyExitedProcess(int exitCode)
    {
        using var output = new GatedReadStream();
        var pty = new ExitingPty(output);
        await using var terminal = new TerminalPty(pty, 80, 24);
        pty.MarkExited(exitCode);
        terminal.CompleteByAgent();
        Assert.False(terminal.CompletedByAgent);
        Assert.Equal(exitCode, terminal.ExitCode);
        Assert.Equal(0, pty.TreeKills);
    }

    [Fact]
    public async Task AgentCompletionDrainsAndPublishesOneNormalExit()
    {
        using var output = new GatedReadStream();
        var pty = new ExitingPty(output);
        await using var terminal = new TerminalPty(pty, 80, 24, exitDrainWindow: TimeSpan.FromMilliseconds(50));
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Exited += (_, code) => exited.TrySetResult(code);
        terminal.StartReadLoop();
        terminal.CompleteByAgent();
        Assert.Equal(1, pty.TreeKills);
        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(terminal.HasExited);
    }
    [Fact]
    public async Task ProcessExitEndsTheSessionWhenTheOutputPipeNeverReachesEof()
    {
        var ct = TestContext.Current.CancellationToken;
        using var output = new GatedReadStream();
        var pty = new ExitingPty(output);
        await using var terminal = new TerminalPty(pty, 80, 24, exitDrainWindow: TimeSpan.FromMilliseconds(100));
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Exited += (_, code) => exited.TrySetResult(code);
        var consumer = new ClosedCountingConsumer();
        terminal.Subscribe(consumer);
        terminal.StartReadLoop();

        pty.RaiseExited(7);

        Assert.Equal(7, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.True(terminal.HasExited);
        Assert.Equal(1, consumer.ClosedCount);
        Assert.False(output.Released, "the forced exit must not depend on the pipe ever closing");
    }

    [Fact]
    public async Task EofInsideTheDrainWindowKeepsTheOrdinaryExitPathAndFiresOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        using var output = new GatedReadStream();
        var pty = new ExitingPty(output) { ExitCode = 3 };
        await using var terminal = new TerminalPty(pty, 80, 24, exitDrainWindow: TimeSpan.FromSeconds(5));
        var exits = new ConcurrentQueue<int>();
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Exited += (_, code) => { exits.Enqueue(code); first.TrySetResult(code); };
        var consumer = new ClosedCountingConsumer();
        terminal.Subscribe(consumer);
        terminal.StartReadLoop();

        pty.RaiseExited(3);
        output.Release(); // Trailing output drained and EOF arrived well inside the window.

        Assert.Equal(3, await first.Task.WaitAsync(TimeSpan.FromSeconds(10), ct));
        await Task.Delay(300, ct); // The drain watcher must notice the read loop ended and stand down.
        Assert.Equal([3], exits);
        Assert.Equal(1, consumer.ClosedCount);
    }

    [Fact]
    public async Task LateEofAfterTheForcedExitDoesNotFireExitedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var output = new GatedReadStream();
        var pty = new ExitingPty(output);
        await using var terminal = new TerminalPty(pty, 80, 24, exitDrainWindow: TimeSpan.FromMilliseconds(50));
        var exits = new ConcurrentQueue<int>();
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Exited += (_, code) => { exits.Enqueue(code); first.TrySetResult(code); };
        var consumer = new ClosedCountingConsumer();
        terminal.Subscribe(consumer);
        terminal.StartReadLoop();

        pty.RaiseExited(0);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        output.Release(); // The orphaned client finally let go; the read loop ends after the fact.
        await Task.Delay(300, ct);

        Assert.Single(exits);
        Assert.Equal(1, consumer.ClosedCount);
    }

    [Fact]
    public async Task ProcessThatExitedBeforeTheTerminalSubscribedStillEndsTheSession()
    {
        // Review finding on VIBE-45: ProcessExited is one-shot and Pty.Net arms it before the connection is
        // handed over, so the event can already be gone when Terminal subscribes. The constructor probes
        // WaitForExit(0) and routes an already-exited PTY through the same drain path.
        var ct = TestContext.Current.CancellationToken;
        using var output = new GatedReadStream();
        var pty = new ExitingPty(output);
        pty.MarkExited(9);
        await using var terminal = new TerminalPty(pty, 80, 24, exitDrainWindow: TimeSpan.FromMilliseconds(50));
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Exited += (_, code) => exited.TrySetResult(code);
        terminal.StartReadLoop();

        Assert.Equal(9, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.True(terminal.HasExited);
    }

    [Fact]
    public async Task LivePtyIsNotEndedByTheConstructorProbe()
    {
        var ct = TestContext.Current.CancellationToken;
        using var output = new GatedReadStream();
        var pty = new ExitingPty(output);
        await using var terminal = new TerminalPty(pty, 80, 24, exitDrainWindow: TimeSpan.FromMilliseconds(50));
        var exits = 0;
        terminal.Exited += (_, _) => Interlocked.Increment(ref exits);
        terminal.StartReadLoop();

        await Task.Delay(300, ct);

        Assert.False(terminal.HasExited);
        Assert.Equal(0, exits);
    }

    [Fact]
    public async Task ProcessExitBeforeTheReadLoopStartsStillEndsTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        using var output = new GatedReadStream();
        var pty = new ExitingPty(output);
        await using var terminal = new TerminalPty(pty, 80, 24, exitDrainWindow: TimeSpan.FromMilliseconds(50));
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Exited += (_, code) => exited.TrySetResult(code);

        pty.RaiseExited(1);

        Assert.Equal(1, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.True(terminal.HasExited);
    }

    private sealed class ExitingPty(Stream reader) : IPtyConnection
    {
        private volatile bool _exited;

        public event EventHandler<PtyExitedEventArgs>? ProcessExited;
        public Stream ReaderStream => reader;
        public Stream WriterStream => Stream.Null;
        public int Pid => 4242;
        public int ExitCode { get; set; }
        public bool WaitForExit(int milliseconds) => _exited;
        public void Kill() { }
        public int TreeKills { get; private set; }
        public void KillProcessTree() { TreeKills++; RaiseExited(137); }
        public void Resize(int cols, int rows) { }
        public void Dispose() => reader.Dispose();

        /// <summary>The process is gone and its one-shot event has already fired, with nobody listening.</summary>
        public void MarkExited(int exitCode)
        {
            ExitCode = exitCode;
            _exited = true;
        }

        public void RaiseExited(int exitCode)
        {
            MarkExited(exitCode);
            ProcessExited?.Invoke(this, ExitArgs(exitCode));
        }

        // Pty.Net is vendored and frozen (its vc.rules.md STOPs every change there), so the internal
        // constructor is reached by reflection rather than by granting Tests InternalsVisibleTo.
        private static PtyExitedEventArgs ExitArgs(int exitCode) => (PtyExitedEventArgs)Activator.CreateInstance(
            typeof(PtyExitedEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, binder: null, [exitCode], culture: null)!;
    }

    /// <summary>A pipe that holds every read until <see cref="Release"/> (then EOF) or disposal (then a read error).</summary>
    private sealed class GatedReadStream : Stream
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Released => _gate.Task.IsCompletedSuccessfully;
        public void Release() => _gate.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            _gate.TrySetException(new ObjectDisposedException(nameof(GatedReadStream)));
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ClosedCountingConsumer : ITerminalConsumer
    {
        private int _closed;
        public int ClosedCount => Volatile.Read(ref _closed);
        public void OnOutput(ReadOnlyMemory<byte> data) { }
        public void OnClosed() => Interlocked.Increment(ref _closed);
    }
}
