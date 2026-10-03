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
        public event EventHandler<PtyExitedEventArgs>? ProcessExited;
        public Stream ReaderStream => reader;
        public Stream WriterStream => Stream.Null;
        public int Pid => 4242;
        public int ExitCode { get; set; }
        public bool WaitForExit(int milliseconds) => true;
        public void Kill() { }
        public void KillProcessTree() { }
        public void Resize(int cols, int rows) { }
        public void Dispose() => reader.Dispose();
        public void RaiseExited(int exitCode) => ProcessExited?.Invoke(this, ExitArgs(exitCode));

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
