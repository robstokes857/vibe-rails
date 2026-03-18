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
        ITerminalConsumer[] snapshot;
        lock (_subscriberLock)
            snapshot = [.. _consumers];
        foreach (var c in snapshot)
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

    /// <summary>
    /// Serializes the full emulator state (scrollback + current screen) to an ANSI byte stream.
    /// On reconnect, xterm.js gets a hard reset then the complete history — scroll up to see
    /// everything, current screen is at the bottom. No DB, no animation, instant.
    /// </summary>
    public byte[] GetGridReplay()
    {
        TerminalEmulator.TerminalCell[][] scrollback;
        TerminalEmulator.TerminalCell[,] snap;
        int rows, cols, cursorRow, cursorCol, cursorShape;
        bool cursorVisible, syncOutputActive;
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
            syncOutputActive = _emulator.SyncOutputActive;
        }

        if (syncOutputActive)
            Log.Warning("[Terminal] Snapshot taken during synchronized output — replay may capture mid-frame state");

        return TerminalGridSerializer.Serialize(scrollback, snap, rows, cols, cursorRow, cursorCol, cursorVisible, cursorShape);
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
