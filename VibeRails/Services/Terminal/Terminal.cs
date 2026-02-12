using Pty.Net;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Unified PTY abstraction. Owns the PTY process, runs a single read loop,
/// and dispatches output to all registered consumers via pub/sub.
/// Thread-safe subscriber management. Both CLI and Web paths use this class.
/// </summary>
public sealed class Terminal : IAsyncDisposable
{
    private readonly IPtyConnection _pty;
    private readonly CircularBuffer _outputBuffer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _subscriberLock = new();
    private readonly List<ITerminalConsumer> _consumers = [];
    private Task? _readLoop;
    private bool _disposed;

    public int Pid => _pty.Pid;
    public int ExitCode => _pty.ExitCode;

    private Terminal(IPtyConnection pty, int replayBufferSize)
    {
        _pty = pty;
        _outputBuffer = new CircularBuffer(replayBufferSize);
    }

    /// <summary>
    /// Spawn a PTY and return a Terminal ready for subscribers and StartReadLoop().
    /// </summary>
    public static async Task<Terminal> CreateAsync(
        string workingDirectory,
        IDictionary<string, string> environment,
        int cols = 120,
        int rows = 30,
        int replayBufferSize = 16384,
        string? title = null,
        CancellationToken ct = default)
    {
        var shell = OperatingSystem.IsWindows() ? "pwsh.exe" : "bash";
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
        var terminal = new Terminal(pty, replayBufferSize);

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
    /// Resize the PTY dimensions.
    /// </summary>
    public void Resize(int cols, int rows) => _pty.Resize(cols, rows);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();

        if (_readLoop != null)
        {
            try { await _readLoop; }
            catch (OperationCanceledException) { }
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
                        Console.Error.WriteLine($"[Terminal] Consumer error: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Terminal] Read loop error: {ex.Message}");
        }
    }

    private sealed class Unsubscriber(Terminal terminal, ITerminalConsumer consumer) : IDisposable
    {
        public void Dispose() => terminal.Unsubscribe(consumer);
    }
}
