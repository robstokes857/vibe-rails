using Pty.Net;
using Serilog;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Unified PTY abstraction. Owns the PTY process, runs a single read loop,
/// and dispatches output to all registered consumers via pub/sub.
/// Thread-safe subscriber management. Both CLI and Web paths use this class.
/// </summary>
public sealed class Terminal : IAsyncDisposable
{
    public const int DefaultCols = 120;
    public const int DefaultRows = 30;
    private const int EmulatorScrollbackLines = 20000;

    private readonly IPtyConnection _pty;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _subscriberLock = new();
    private readonly List<ITerminalConsumer> _consumers = [];
    private readonly TerminalEmulator.Terminal _emulator;
    private readonly Lock _emulatorLock = new();
    private Task? _readLoop;
    private bool _disposed;
    private int _hasExited;
    private int _cols;
    private int _rows;

    public int Pid => _pty.Pid;
    public int ExitCode => _pty.ExitCode;
    public int Cols => _cols;
    public int Rows => _rows;
    public bool HasExited => Volatile.Read(ref _hasExited) == 1;
    public bool IsSyncOutputActive
    {
        get
        {
            lock (_emulatorLock)
                return _emulator.SyncOutputActive;
        }
    }

    public static string GetDefaultShellPath() => OperatingSystem.IsWindows() ? "pwsh.exe" : "bash";

    /// <summary>
    /// Event fired when the PTY process exits (naturally or via kill).
    /// </summary>
    public event EventHandler<int>? Exited;

    private Terminal(IPtyConnection pty, int cols, int rows)
    {
        _pty = pty;
        _emulator = new TerminalEmulator.Terminal(cols: cols, rows: rows, scrollbackSize: EmulatorScrollbackLines);
        _cols = cols;
        _rows = rows;
    }

    /// <summary>
    /// Spawn a PTY and return a Terminal ready for subscribers and StartReadLoop().
    /// </summary>
    public static async Task<Terminal> CreateAsync(
        string workingDirectory,
        IDictionary<string, string> environment,
        int cols = DefaultCols,
        int rows = DefaultRows,
        string? title = null,
        CancellationToken ct = default)
    {
        var shell = GetDefaultShellPath();
        var options = new PtyOptions
        {
            Name = title ?? "VibeRails-Terminal",
            Cols = cols,
            Rows = rows,
            Cwd = workingDirectory,
            App = shell,
            CommandLine = [],
            Environment = environment
        };

        var pty = await PtyProvider.SpawnAsync(options, ct);
        var terminal = new Terminal(pty, cols, rows);
        terminal.Subscribe(new TerminalEmulatorConsumer(terminal._emulator, terminal._emulatorLock));

        return terminal;
    }

    /// <summary>
    /// Start the background read loop. Call once after subscribing initial consumers.
    /// </summary>
    public void StartReadLoop()
    {
        if (_readLoop != null)
            throw new InvalidOperationException("Read loop already started");

        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Subscribe a consumer to receive PTY output. Thread-safe.
    /// Returns an IDisposable that unsubscribes when disposed.
    /// </summary>
    public IDisposable Subscribe(ITerminalConsumer consumer)
    {
        lock (_subscriberLock)
        {
            _consumers.Add(consumer);
        }
        return new Unsubscriber(this, consumer);
    }

    /// <summary>
    /// Atomically captures the current emulator state, delivers it to the consumer
    /// as its first output, and subscribes it for live PTY bytes.
    ///
    /// Guarantee: the consumer's view equals (snapshot at time T) followed by
    /// (every PTY byte dispatched after T). No bytes lost, no bytes duplicated,
    /// no need for any TUI-poking redraw hint on reconnect.
    ///
    /// The whole operation runs under <see cref="_subscriberLock"/> so the read
    /// loop cannot be mid-dispatch while we add the new consumer, and under
    /// <see cref="_emulatorLock"/> so the emulator state cannot change while we
    /// serialize it. Consumers' OnOutput implementations must be non-blocking
    /// per the <see cref="ITerminalConsumer"/> contract.
    /// </summary>
    public IDisposable SubscribeWithSnapshot(ITerminalConsumer consumer)
    {
        lock (_subscriberLock)
        {
            var snapshot = CaptureSnapshotLocked();
            DeliverSnapshot(consumer, snapshot);
            _consumers.Add(consumer);
        }
        return new Unsubscriber(this, consumer);
    }

    /// <summary>
    /// Delivers a fresh emulator snapshot to an already-subscribed consumer.
    /// Used by the remote path on replay-request / post-pin-verify so the viewer
    /// sees an immediate self-healing repaint (the snapshot begins with ED2 + ED3
    /// + cursor home, so any prior state on the remote side is wiped).
    ///
    /// Atomic with respect to the read loop, so no live bytes interleave with
    /// the snapshot. Any bytes dispatched after the snapshot arrive in-order
    /// via the consumer's normal channel.
    /// </summary>
    public void PushSnapshotTo(ITerminalConsumer consumer)
    {
        lock (_subscriberLock)
        {
            var snapshot = CaptureSnapshotLocked();
            DeliverSnapshot(consumer, snapshot);
        }
    }

    // Must be called with _subscriberLock held. Takes _emulatorLock internally
    // so the serializer sees a consistent emulator state.
    private byte[] CaptureSnapshotLocked()
    {
        TerminalEmulator.TerminalCell[][] scrollback;
        TerminalEmulator.TerminalCell[,] snap;
        int rows, cols, cursorRow, cursorCol, cursorShape;
        bool cursorVisible, isAlternateScreen;
        lock (_emulatorLock)
        {
            scrollback = _emulator.GetScrollback();
            snap = _emulator.GetSnapshot();
            rows = _emulator.Rows;
            cols = _emulator.Cols;
            cursorRow = _emulator.CursorRow;
            cursorCol = _emulator.CursorCol;
            cursorVisible = _emulator.CursorVisible;
            cursorShape = _emulator.CursorShape;
            isAlternateScreen = _emulator.IsAlternateScreen;
        }
        return TerminalGridSerializer.Serialize(
            scrollback, snap, rows, cols,
            cursorRow, cursorCol, cursorVisible, cursorShape, isAlternateScreen);
    }

    private static void DeliverSnapshot(ITerminalConsumer consumer, byte[] snapshot)
    {
        if (snapshot.Length == 0) return;
        try
        {
            consumer.OnOutput(snapshot);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Terminal] Consumer error while delivering snapshot");
        }
    }

    /// <summary>
    /// Remove a consumer. Thread-safe.
    /// </summary>
    public void Unsubscribe(ITerminalConsumer consumer)
    {
        lock (_subscriberLock)
        {
            _consumers.Remove(consumer);
        }
    }

    /// <summary>
    /// Write a string to the PTY stdin (encoded as UTF-8).
    /// </summary>
    public async Task WriteAsync(string input, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(input)) return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        await WriteBytesAsync(bytes, ct);
    }

    /// <summary>
    /// Write raw bytes to the PTY stdin.
    /// </summary>
    public async Task WriteBytesAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty) return;
        await _pty.WriterStream.WriteAsync(buffer, ct);
        await _pty.WriterStream.FlushAsync(ct);
    }

    /// <summary>
    /// Send a command to the shell (appends \r and writes).
    /// </summary>
    public Task SendCommandAsync(string command, CancellationToken ct = default)
    {
        return WriteAsync(command + "\r", ct);
    }

    /// <summary>
    /// Inject bytes into the output stream — dispatched to all consumers (native terminal,
    /// remote viewer, emulator) exactly as if the PTY had produced them, without writing
    /// to PTY stdin. Use for ANSI sequences like title updates (OSC) that must travel
    /// the output path to be interpreted by the terminal emulator.
    /// </summary>
    public void PublishOutput(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty) return;
        // Same lock discipline as ReadLoopAsync — hold _subscriberLock across
        // dispatch so injected output is ordered correctly with PTY output and
        // with new subscribers joining via SubscribeWithSnapshot.
        lock (_subscriberLock)
        {
            foreach (var c in _consumers)
            {
                try
                {
                    c.OnOutput(data);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Terminal] Consumer error while publishing synthetic output");
                }
            }
        }
    }

    /// <summary>
    /// Serializes the full emulator state (scrollback + current screen) to an ANSI byte stream.
    /// Prefer <see cref="SubscribeWithSnapshot"/> or <see cref="PushSnapshotTo"/> for attach
    /// flows — they capture + deliver atomically so live bytes cannot interleave with the
    /// snapshot. This raw accessor is kept for diagnostics and out-of-band callers that
    /// handle ordering themselves.
    /// </summary>
    public byte[] GetGridReplay()
    {
        lock (_subscriberLock)
            return CaptureSnapshotLocked();
    }

    /// <summary>
    /// Returns the current screen as plain text lines (no ANSI codes).
    /// </summary>
    public string[] GetScreenText()
    {
        lock (_emulatorLock)
            return _emulator.GetScreenText();
    }

    /// <summary>
    /// Resize the PTY dimensions. Also resizes the emulator to keep grid in sync.
    /// </summary>
    public void Resize(int cols, int rows)
    {
        _pty.Resize(cols, rows);
        _cols = cols;
        _rows = rows;
        lock (_emulatorLock)
            _emulator.Resize(cols, rows);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();

        try
        {
            _pty.Kill();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Terminal] PTY kill during dispose failed or was unnecessary");
        }

        if (_readLoop != null)
        {
            var completed = await Task.WhenAny(_readLoop, Task.Delay(TimeSpan.FromSeconds(1)));
            if (completed == _readLoop)
            {
                try { await _readLoop; }
                catch { }
            }
            else
            {
                Log.Warning("[Terminal] Read loop did not exit promptly after PTY kill");
            }
        }

        _pty.Dispose();
        _cts.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[4096];
        var token = _cts.Token;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var bytesRead = await _pty.ReaderStream.ReadAsync(buffer.AsMemory(), token);
                if (bytesRead == 0) break;

                var data = new ReadOnlyMemory<byte>(buffer, 0, bytesRead);

                // Hold _subscriberLock for the entire dispatch so a new subscriber
                // joining via SubscribeWithSnapshot can only join BETWEEN dispatches,
                // never in the middle of one. That's what makes the snapshot atomic:
                // the new consumer's snapshot reflects emulator state *after* the
                // previous dispatch and *before* the next, and it will receive every
                // subsequent batch in-order. All consumers' OnOutput must be
                // non-blocking per the ITerminalConsumer contract.
                lock (_subscriberLock)
                {
                    foreach (var consumer in _consumers)
                    {
                        try
                        {
                            consumer.OnOutput(data);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "[Terminal] Consumer error");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "[Terminal] Read loop error");
        }
        finally
        {
            // ExitCode can throw if the process hasn't fully exited yet (pipe EOF races process exit).
            // Always invoke Exited — use -1 as fallback so listeners can clean up.
            Interlocked.Exchange(ref _hasExited, 1);
            int exitCode;
            try { exitCode = _pty.ExitCode; }
            catch { exitCode = -1; }

            try { Exited?.Invoke(this, exitCode); }
            catch (Exception ex) { Log.Error(ex, "[Terminal] Error in exit handlers"); }
        }
    }

    private sealed class Unsubscriber(Terminal terminal, ITerminalConsumer consumer) : IDisposable
    {
        public void Dispose() => terminal.Unsubscribe(consumer);
    }
}
