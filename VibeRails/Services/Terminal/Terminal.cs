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
    public const int DefaultReplayBufferSize = 10 * 1024 * 1024;

    private readonly IPtyConnection _pty;
    private readonly CircularBuffer _outputBuffer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _subscriberLock = new();
    private readonly List<ITerminalConsumer> _consumers = [];
    private readonly TerminalEmulator.Terminal _emulator;
    private readonly Lock _emulatorLock = new();
    private Task? _readLoop;
    private bool _disposed;
    private int _cols;
    private int _rows;

    public int Pid => _pty.Pid;
    public int ExitCode => _pty.ExitCode;
    public int Cols => _cols;
    public int Rows => _rows;

    public static string GetDefaultShellPath() => OperatingSystem.IsWindows() ? "pwsh.exe" : "bash";

    /// <summary>
    /// Event fired when the PTY process exits (naturally or via kill).
    /// </summary>
    public event EventHandler<int>? Exited;

    private Terminal(IPtyConnection pty, int replayBufferSize, int cols, int rows)
    {
        _pty = pty;
        _outputBuffer = new CircularBuffer(replayBufferSize);
        _emulator = new TerminalEmulator.Terminal(cols: cols, rows: rows);
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
        int replayBufferSize = DefaultReplayBufferSize,
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
        var terminal = new Terminal(pty, replayBufferSize, cols, rows);
        terminal.Subscribe(new TerminalEmulatorConsumer(terminal._emulator, terminal._emulatorLock));

        // Set terminal title via ANSI escape sequence if provided
        if (!string.IsNullOrEmpty(title))
        {
            var titleSequence = $"\x1b]0;{title}\x07";
            var bytes = System.Text.Encoding.UTF8.GetBytes(titleSequence);
            await pty.WriterStream.WriteAsync(bytes, ct);
            await pty.WriterStream.FlushAsync(ct);
        }

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
    /// Get a snapshot of the replay buffer (last N bytes of output).
    /// Used to send screen state to new WebSocket connections.
    /// </summary>
    public byte[] GetReplayBuffer() => _outputBuffer.GetData();

    /// <summary>
    /// Get replay bytes starting from the last ANSI break point (alternate screen enter,
    /// erase display, or full reset). Falls back to full GetReplayBuffer() if none recorded.
    /// </summary>
    public byte[] GetReplayBufferFromLastBreakPoint() => _outputBuffer.GetDataFromLastBreakPoint();

    /// <summary>
    /// Serializes the current emulator grid to an ANSI byte stream.
    /// xterm.js clients replay this to reconstruct the exact current screen state.
    /// Always returns a valid screen — no break-point heuristics required.
    /// </summary>
    public byte[] GetGridReplay()
    {
        TerminalEmulator.TerminalCell[,] snap;
        int rows, cols, cursorRow, cursorCol;
        lock (_emulatorLock)
        {
            snap = _emulator.GetSnapshot();
            rows = _emulator.Rows;
            cols = _emulator.Cols;
            cursorRow = _emulator.CursorRow;
            cursorCol = _emulator.CursorCol;
        }
        return TerminalGridSerializer.Serialize(snap, rows, cols, cursorRow, cursorCol);
    }

    /// <summary>
    /// Returns the current screen as plain text (no ANSI codes), useful for logging/snapshots.
    /// Each element is one row of text.
    /// </summary>
    public string[] GetScreenText()
    {
        lock (_emulatorLock)
            return _emulator.GetScreenText();
    }

    /// <summary>
    /// Inject synthetic bytes directly into the output stream and replay buffer.
    /// Use sparingly — this bypasses the PTY and writes directly to all consumers.
    /// Capped at 4 KB to prevent buffer exhaustion; caller is responsible for content.
    /// </summary>
    internal void PublishSynthetic(ReadOnlyMemory<byte> data)
    {
        const int maxSyntheticBytes = 4096;
        if (data.Length > maxSyntheticBytes)
        {
            Log.Warning("[Terminal] PublishSynthetic oversized payload ({Bytes} bytes) — truncating", data.Length);
            data = data[..maxSyntheticBytes];
        }

        _outputBuffer.Append(data.Span);

        ITerminalConsumer[] snapshot;
        lock (_subscriberLock)
        {
            snapshot = [.. _consumers];
        }

        foreach (var consumer in snapshot)
        {
            try { consumer.OnOutput(data); }
            catch { }
        }
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

        if (_readLoop != null)
        {
            try { await _readLoop; }
            catch { }
        }

        _pty.Kill();
        _pty.Dispose();
        _cts.Dispose();
        _outputBuffer.Clear();
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

                // Always buffer for replay
                _outputBuffer.Append(data.Span);

                // Snapshot consumers under lock, iterate outside
                ITerminalConsumer[] snapshot;
                lock (_subscriberLock)
                {
                    snapshot = [.. _consumers];
                }

                foreach (var consumer in snapshot)
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "[Terminal] Read loop error");
        }
        finally
        {
            // Notify listeners that the PTY has exited.
            // ExitCode can throw if the process hasn't fully exited yet (pipe EOF races the process exit).
            // Always invoke Exited regardless — use -1 as a fallback so listeners can clean up.
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
