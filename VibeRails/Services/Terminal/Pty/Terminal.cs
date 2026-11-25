using Pty.Net;
using Serilog;
using System.Text;
using VibeRails.Services.Terminal.Consumers;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public sealed record TerminalSnapshotData(
    int Cols,
    int Rows,
    string[] ScreenText,
    byte[] XtermReplayBytes);

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
    private readonly SemaphoreSlim _stdinWriteGate = new(1, 1);
    private readonly CodexWindowsInputRewriter _codexWindowsInputRewriter = new();
    private static readonly byte[] s_win32EscapeKeyBytes =
        System.Text.Encoding.ASCII.GetBytes(CodexWindowsInputRewriter.Win32EscapeKey);
    private static readonly byte[] s_escapeKeyBytes = [0x1B];
    private readonly Lock _subscriberLock = new();
    private readonly List<ITerminalConsumer> _consumers = [];
    private readonly TerminalEmulator.Terminal _emulator;
    private readonly Lock _emulatorLock = new();
    private readonly TimeSpan _exitDrainWindow;
    private Task? _readLoop;
    private bool _disposed;
    // Guarded by _subscriberLock. A provider may leave a read pending even after disposal.
    private bool _outputQuiesced;
    private bool _consumersClosed;
    private int _hasExited;
    private int _agentCompletion;
    private int _cols;
    private int _rows;
    // Set only for a PTY whose console host forwards terminal queries (a bundled conpty.dll).
    private TerminalQueryResponder? _queryResponder;
    private QueryAnsweringEmulatorConsumer? _queryConsumer;

    /// <summary>
    /// How long the read loop may keep draining after the PTY process itself has exited before the
    /// session is ended without waiting for EOF. ConPTY keeps its output pipe open while any client
    /// is still attached to the console, so a shell that has exited would otherwise leave the session
    /// "active" for as long as an orphaned descendant (a compiler server, an MCP server) lives.
    /// </summary>
    internal static readonly TimeSpan DefaultExitDrainWindow = TimeSpan.FromSeconds(2);

    public int Pid => _pty.Pid;
    public int ExitCode => Volatile.Read(ref _agentCompletion) != 0 ? 0 : _pty.ExitCode;
    public int Cols => _cols;
    public int Rows => _rows;
    public bool HasExited => Volatile.Read(ref _hasExited) == 1;
    /// <summary>True only when this PTY accepted an explicit agent completion.</summary>
    public bool CompletedByAgent => Volatile.Read(ref _agentCompletion) == 1;
    internal string? AgentCompletionReason => Volatile.Read(ref _agentCompletion) switch
    {
        1 => DTOs.AgentSessionClosedPayload.Reason,
        2 => DTOs.AgentSessionClosedPayload.IdleReason,
        _ => null
    };
    public bool EncodeBareLineFeedAsWin32ShiftEnter { get; set; }

    /// <summary>
    /// The console host behind the PTY. <see cref="PtyConsoleHost.Bundled"/> means output reaches
    /// viewers as the program wrote it and this terminal answers device queries itself, so a
    /// viewer must not (VIBE-354). <see cref="PtyConsoleHost.None"/> outside Windows.
    /// </summary>
    public PtyConsoleHost ConsoleHost => _pty.ConsoleHost;

    /// <summary>Device-attribute and device-status queries this terminal has answered. Zero unless the host is bundled.</summary>
    internal long AnsweredTerminalQueries => _queryResponder?.AnsweredCount ?? 0;

    public bool IsSyncOutputActive
    {
        get
        {
            lock (_emulatorLock)
                return _emulator.SyncOutputActive;
        }
    }

    /// <summary>
    /// True while the CLI is on the alternate screen buffer — i.e. a full-screen TUI is painting.
    /// Used to decide whether a post-resize repaint is warranted: alt-screen TUIs are diff
    /// renderers and cannot recover from a host buffer being reflowed under them, whereas a shell
    /// on the normal buffer reflows correctly and must not be cleared.
    /// </summary>
    public bool IsAlternateScreen
    {
        get
        {
            lock (_emulatorLock)
                return _emulator.IsAlternateScreen;
        }
    }

    public static string GetDefaultShellPath() => ShellDefaults.GetDefaultPtyShellPath();

    /// <summary>
    /// Event fired when the PTY process exits (naturally or via kill).
    /// </summary>
    public event EventHandler<int>? Exited;

    /// <summary>
    /// Raised after every resize of this PTY, with the surface that asked for it. The remote
    /// viewer uses it to send a viewer that takes its grid from snapshots the new screen (VIBE-172).
    /// </summary>
    public event Action<TerminalIoSource>? Resized;

    internal Terminal(IPtyConnection pty, int cols, int rows, TimeSpan? exitDrainWindow = null)
    {
        _pty = pty;
        _emulator = new TerminalEmulator.Terminal(cols: cols, rows: rows, scrollbackSize: EmulatorScrollbackLines);
        _cols = cols;
        _rows = rows;
        _exitDrainWindow = exitDrainWindow ?? DefaultExitDrainWindow;
        _pty.ProcessExited += OnPtyProcessExited;
        // ProcessExited is a one-shot notification and Pty.Net arms its process watcher before the
        // connection is handed over, so a fast exit can land before the subscription above. Probe
        // once, after subscribing, so an already-exited PTY takes the same bounded-drain path.
        if (HasPtyProcessExited())
            BeginExitWatch();
    }

    private bool HasPtyProcessExited()
    {
        try { return _pty.WaitForExit(0); }
        catch { return false; }
    }

    private int ReadPtyExitCode()
    {
        // ExitCode can throw if the process hasn't fully exited yet (pipe EOF races process exit).
        // Always invoke Exited — use -1 as fallback so listeners can clean up.
        try { return ExitCode; }
        catch { return -1; }
    }

    private void OnPtyProcessExited(object? sender, PtyExitedEventArgs e) => BeginExitWatch();

    /// <summary>
    /// The PTY process is gone, so the session is over whether or not the output pipe ever reaches
    /// EOF. Trailing output is usually still in flight, so the read loop gets <see cref="_exitDrainWindow"/>
    /// to drain it and end on its own; after that <see cref="Exited"/> is raised from here (VIBE-45: an
    /// Automation wrapper shell whose `dotnet test` left a compiler server attached to the console kept
    /// its finished agent "Running" for hours because EOF never came). Reached from the exit event and
    /// from the constructor's probe; <see cref="RaiseExitedOnce"/> makes a second arrival harmless.
    /// </summary>
    private void BeginExitWatch()
    {
        if (HasExited || _disposed)
            return;

        var readLoop = _readLoop;
        _ = Task.Run(async () =>
        {
            try
            {
                var drain = Task.Delay(_exitDrainWindow);
                if (readLoop is not null && await Task.WhenAny(readLoop, drain) == readLoop)
                    return;
                if (readLoop is null)
                    await drain;
                if (HasExited || _disposed)
                    return;

                int pid;
                try { pid = _pty.Pid; } catch { pid = -1; }
                var exitCode = ReadPtyExitCode();
                Log.Information(
                    "[Terminal] PTY process {Pid} exited with code {ExitCode} but its output pipe stayed open; ending the session without waiting for EOF",
                    pid, exitCode);
                RaiseExitedOnce(exitCode);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Terminal] PTY exit watcher failed");
            }
        });
    }

    /// <summary>
    /// Marks the terminal exited, lets consumers flush, and fires <see cref="Exited"/> exactly once,
    /// whichever of the read loop's EOF or the PTY process exit gets here first.
    /// </summary>
    private void RaiseExitedOnce(int exitCode)
    {
        if (Interlocked.Exchange(ref _hasExited, 1) == 1)
            return;

        NotifyConsumersClosed();

        try { Exited?.Invoke(this, exitCode); }
        catch (Exception ex) { Log.Error(ex, "[Terminal] Error in exit handlers"); }
    }

    /// <summary>
    /// Spawn a PTY and return a Terminal ready for subscribers and StartReadLoop().
    ///
    /// By default the PTY runs the user's interactive shell with no arguments, and the caller types
    /// what it wants into it. Pass <paramref name="app"/>/<paramref name="argv"/> to run a specific
    /// program instead — the Job path does this so the PTY terminates when the program does, rather
    /// than sitting at a shell prompt forever.
    ///
    /// <paramref name="bundledConsoleHostPath"/> names a bundled conpty.dll to host the PTY with
    /// on Windows (see <see cref="BundledConsoleHost"/>). Null, the default, is the ConPTY built
    /// into Windows. The built-in host is also what a terminal gets when the bundled one cannot be
    /// used; the log says which host each such terminal got and why.
    /// </summary>
    public static async Task<Terminal> CreateAsync(
        string workingDirectory,
        IDictionary<string, string> environment,
        int cols = DefaultCols,
        int rows = DefaultRows,
        string? title = null,
        CancellationToken ct = default,
        string? app = null,
        IReadOnlyList<string>? argv = null,
        string? bundledConsoleHostPath = null)
    {
        var executable = string.IsNullOrWhiteSpace(app) ? GetDefaultShellPath() : app;
        var options = new PtyOptions
        {
            Name = string.IsNullOrWhiteSpace(title) ? GetFallbackTerminalName(workingDirectory) : title,
            Cols = cols,
            Rows = rows,
            Cwd = workingDirectory,
            App = executable,
            CommandLine = argv is { Count: > 0 } ? [.. argv] : [],
            Environment = environment,
            WindowsConPtyLibraryPath = bundledConsoleHostPath
        };

        var pty = await PtyProvider.SpawnAsync(options, ct);
        LogConsoleHost(pty, bundledConsoleHostPath);
        var terminal = new Terminal(pty, cols, rows);
        terminal.AttachEmulator();

        return terminal;
    }

    /// <summary>
    /// One line for every Windows PTY, naming the console host it got (VIBE-354). A fallback to
    /// the built-in host is silent everywhere else: the terminal works, with the re-rendered
    /// output the setting was turned on to avoid, so the warning is the only evidence of it.
    /// Other platforms have no console host and log nothing.
    /// </summary>
    private static void LogConsoleHost(IPtyConnection pty, string? requestedPath)
    {
        int pid;
        try { pid = pty.Pid; } catch { pid = -1; }
        if (DescribeConsoleHost(pty.ConsoleHost, pid, requestedPath, pty.ConsoleHostFallbackReason) is { } entry)
            Log.Write(entry.Level, entry.Template, entry.Values);
    }

    /// <summary>The log entry for a new PTY's console host; null where there is no host to name.</summary>
    internal static (Serilog.Events.LogEventLevel Level, string Template, object?[] Values)? DescribeConsoleHost(
        PtyConsoleHost host, int pid, string? requestedPath, string? fallbackReason)
    {
        if (host == PtyConsoleHost.Bundled)
        {
            return (Serilog.Events.LogEventLevel.Information,
                "[Terminal] PTY {Pid} is hosted by the bundled console host {Path}", [pid, requestedPath]);
        }

        if (requestedPath is not null)
        {
            return (Serilog.Events.LogEventLevel.Warning,
                "[Terminal] PTY {Pid} is hosted by the built-in console host although the modern host is turned on: {Reason}",
                [pid, fallbackReason ?? "this platform has no bundled console host"]);
        }

        return host == PtyConsoleHost.Inbox
            ? (Serilog.Events.LogEventLevel.Information, "[Terminal] PTY {Pid} is hosted by the built-in console host", [pid])
            : null;
    }

    /// <summary>
    /// Subscribes the headless emulator. Under a bundled console host the same consumer also
    /// answers the device queries that host forwards; under any other host nothing changes.
    /// Call once, before the read loop starts.
    /// </summary>
    internal void AttachEmulator()
    {
        if (_pty.ConsoleHost == PtyConsoleHost.Bundled)
        {
            _queryResponder = new TerminalQueryResponder(WriteBytesAsync, _cts.Token);
            _queryConsumer = new QueryAnsweringEmulatorConsumer(_emulator, _emulatorLock, _queryResponder);
            Subscribe(_queryConsumer);
            return;
        }

        Subscribe(new TerminalEmulatorConsumer(_emulator, _emulatorLock));
    }

    private static string GetFallbackTerminalName(string workingDirectory)
    {
        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(workingDirectory);
            var folderName = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(folderName) ? "Terminal" : folderName;
        }
        catch
        {
            return "Terminal";
        }
    }

    /// <summary>
    /// Start the background read loop. Call once after subscribing initial consumers.
    /// </summary>
    public void StartReadLoop()
    {
        if (_readLoop != null)
            throw new InvalidOperationException("Read loop already started");

        var token = _cts.Token;
        _readLoop = Task.Run(() => ReadLoopAsync(token));
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
    /// <returns>The grid the snapshot paints.</returns>
    public (int Cols, int Rows) PushSnapshotTo(ITerminalConsumer consumer) => PushSnapshotTo(consumer, includeScrollback: true, int.MaxValue);

    /// <summary>
    /// <see cref="PushSnapshotTo(ITerminalConsumer)"/> for a consumer that takes at most
    /// <paramref name="maxBytes"/> in one message: the oldest scrollback rows are left out until the
    /// snapshot fits. The screen is always sent whole. The relay closes a desktop socket whose message
    /// passes its limit, so every remote snapshot is sent this way (VIBE-177).
    /// </summary>
    /// <returns>The grid the snapshot paints.</returns>
    public (int Cols, int Rows) PushSnapshotTo(ITerminalConsumer consumer, int maxBytes) => PushSnapshotTo(consumer, includeScrollback: true, maxBytes);

    /// <summary>
    /// <see cref="PushSnapshotTo(ITerminalConsumer)"/> without the scrollback rows: the same reset
    /// prologue, then the current screen. For a viewer that keeps no history and only has to
    /// learn the screen at a new size (VIBE-172). It is a fraction of a full snapshot's size, but
    /// the prologue still clears a viewer's scrollback, so send it only to a viewer that asked.
    /// </summary>
    /// <returns>The grid the snapshot paints.</returns>
    public (int Cols, int Rows) PushScreenSnapshotTo(ITerminalConsumer consumer) => PushSnapshotTo(consumer, includeScrollback: false, int.MaxValue);

    private (int Cols, int Rows) PushSnapshotTo(ITerminalConsumer consumer, bool includeScrollback, int maxBytes)
    {
        lock (_subscriberLock)
        {
            var snapshot = CaptureSnapshotLocked(includeScrollback, maxBytes, out var cols, out var rows);
            if (snapshot.Length > maxBytes)
                Log.Warning("[Terminal] A {Cols}x{Rows} snapshot is {Bytes} bytes without history, over the {MaxBytes}-byte limit",
                    cols, rows, snapshot.Length, maxBytes);
            DeliverSnapshot(consumer, snapshot);
            return (cols, rows);
        }
    }

    // Must be called with _subscriberLock held. Takes _emulatorLock internally
    // so the serializer sees a consistent emulator state.
    private byte[] CaptureSnapshotLocked() => CaptureSnapshotLocked(includeScrollback: true, int.MaxValue, out _, out _);

    private byte[] CaptureSnapshotLocked(bool includeScrollback, int maxBytes, out int cols, out int rows)
    {
        TerminalEmulator.TerminalCell[][] scrollback;
        TerminalEmulator.TerminalCell[,] snap;
        int cursorRow, cursorCol, cursorShape;
        bool cursorVisible, isAlternateScreen, bracketedPaste;
        int[] inputReportingModes;
        lock (_emulatorLock)
        {
            scrollback = includeScrollback ? _emulator.GetScrollback() : [];
            snap = _emulator.GetSnapshot();
            rows = _emulator.Rows;
            cols = _emulator.Cols;
            cursorRow = _emulator.CursorRow;
            cursorCol = _emulator.CursorCol;
            cursorVisible = _emulator.CursorVisible;
            cursorShape = _emulator.CursorShape;
            isAlternateScreen = _emulator.IsAlternateScreen;
            bracketedPaste = _emulator.BracketedPasteActive;
            inputReportingModes = _emulator.GetInputReportingModes();
        }
        return TerminalGridSerializer.Serialize(
            scrollback, snap, rows, cols,
            cursorRow, cursorCol, cursorVisible, cursorShape, isAlternateScreen, bracketedPaste,
            inputReportingModes, maxBytes);
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
    /// True while a local web viewer (a <see cref="WebSocketConsumer"/>) is subscribed.
    /// While one is attached it owns the PTY geometry: xterm.js is never told about
    /// geometry changes it didn't request (there is no resize rebroadcast to viewers),
    /// so a foreign resize makes it wrap frames composed for a different width —
    /// shredded chrome + stranded cursor.
    /// </summary>
    public bool HasLocalWebViewer
    {
        get
        {
            lock (_subscriberLock)
            {
                foreach (var consumer in _consumers)
                {
                    if (consumer is WebSocketConsumer)
                        return true;
                }
                return false;
            }
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

        await _stdinWriteGate.WaitAsync(ct);
        try
        {
            // This write did not pass through the routed-input parser. It therefore
            // breaks CR adjacency and any partial paste marker, while an established
            // bracketed-paste region remains active until its routed ESC[201~ arrives.
            _codexWindowsInputRewriter.BreakInputSequence();
            await WriteBytesCoreAsync(buffer, ct);
        }
        finally
        {
            _stdinWriteGate.Release();
        }
    }

    /// <summary>
    /// Writes semantic user input to the PTY. The stateful Codex byte rewrite and
    /// the physical stream write share one gate, so concurrent local/remote input
    /// cannot advance parser state in a different order than bytes reach ConPTY.
    /// </summary>
    internal async Task WriteInputBytesAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty) return;

        await _stdinWriteGate.WaitAsync(ct);
        try
        {
            await WriteInputBytesCoreAsync(buffer, ct);
        }
        finally
        {
            _stdinWriteGate.Release();
        }
    }

    /// <summary>
    /// Writes a known physical Escape key. A raw input chunk containing ESC is
    /// deliberately not treated as this event because it may begin a split CSI.
    /// Shares the raw-write gate and sequence boundary handling with other input.
    /// </summary>
    internal async Task WriteEscapeKeyAsync(CancellationToken ct = default)
    {
        await _stdinWriteGate.WaitAsync(ct);
        try
        {
            await WriteEscapeKeyCoreAsync(ct);
        }
        finally
        {
            _stdinWriteGate.Release();
        }
    }

    /// <summary>
    /// Reserves stdin for a whole automated sequence, including its waits. The scoped writer
    /// bypasses gate acquisition; ordinary raw, routed and physical-key writes all wait here.
    /// </summary>
    internal async Task RunInputBatchAsync(Func<InputBatch, CancellationToken, Task> action, CancellationToken ct = default)
    {
        await _stdinWriteGate.WaitAsync(ct);
        var writer = new InputBatch(this);
        try { await action(writer, ct); }
        finally
        {
            writer.Close();
            _stdinWriteGate.Release();
        }
    }

    internal sealed class InputBatch(Terminal terminal)
    {
        private bool _closed;
        internal void Close() => _closed = true;
        internal Task WriteInputBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return terminal.WriteInputBytesCoreAsync(bytes, ct);
        }
        internal Task WriteEscapeKeyAsync(CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return terminal.WriteEscapeKeyCoreAsync(ct);
        }
    }

    private Task WriteInputBytesCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        var ptyBytes = EncodeBareLineFeedAsWin32ShiftEnter ? _codexWindowsInputRewriter.Rewrite(buffer) : buffer;
        return WriteBytesCoreAsync(ptyBytes, ct);
    }

    private Task WriteEscapeKeyCoreAsync(CancellationToken ct)
    {
        // Sending Win32 Escape before the first rewritten LF would switch ConPTY's parser.
        // Both the state check and physical write run under the caller's stdin reservation.
        var bytes = EncodeBareLineFeedAsWin32ShiftEnter && _codexWindowsInputRewriter.HasRewrittenLineFeed
            ? s_win32EscapeKeyBytes : s_escapeKeyBytes;
        _codexWindowsInputRewriter.BreakInputSequence();
        return WriteBytesCoreAsync(bytes, ct);
    }

    private async Task WriteBytesCoreAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
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
            if (_outputQuiesced) return;
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
    /// Captures the CURRENT screen only (no scrollback) for out-of-band snapshot consumers.
    /// Serializing the full scrollback
    /// here can balloon a single tool response to megabytes of base64, and a caller
    /// that just wants the visible screen never needs the history. Only
    /// <see cref="_emulatorLock"/> is taken (a read-only grid copy), so snapshot
    /// polling cannot stall the live read loop, which holds <see cref="_subscriberLock"/>
    /// to dispatch output.
    /// </summary>
    public TerminalSnapshotData CaptureSnapshotData() => CaptureSnapshotData(completed: false);

    /// <summary>
    /// Captures the final screen and bounded scrollback as a read-only main-screen view.
    /// Mouse reporting, alternate-screen and input modes are not restored in this view.
    /// </summary>
    internal TerminalSnapshotData CaptureCompletedSnapshotData() => CaptureSnapshotData(completed: true);

    private TerminalSnapshotData CaptureSnapshotData(bool completed)
    {
        TerminalEmulator.TerminalCell[,] snap;
        TerminalEmulator.TerminalCell[][] scrollback;
        int rows, cols, cursorRow, cursorCol, cursorShape;
        bool cursorVisible, isAlternateScreen, bracketedPaste;

        lock (_emulatorLock)
        {
            snap = _emulator.GetSnapshot();
            rows = _emulator.Rows;
            cols = _emulator.Cols;
            cursorRow = _emulator.CursorRow;
            cursorCol = _emulator.CursorCol;
            cursorVisible = _emulator.CursorVisible;
            cursorShape = _emulator.CursorShape;
            isAlternateScreen = _emulator.IsAlternateScreen;
            bracketedPaste = _emulator.BracketedPasteActive;
            scrollback = completed ? _emulator.GetScrollback() : [];
        }

        var replayBytes = TerminalGridSerializer.Serialize(
            scrollback, snap, rows, cols,
            cursorRow, cursorCol, !completed && cursorVisible, cursorShape,
            !completed && isAlternateScreen, !completed && bracketedPaste);

        return new TerminalSnapshotData(
            cols,
            rows,
            GetScreenTextFromSnapshot(snap, rows, cols),
            replayBytes);
    }

    /// <summary>
    /// Returns the current screen as plain text lines (no ANSI codes).
    /// </summary>
    public string[] GetScreenText()
    {
        lock (_emulatorLock)
            return _emulator.GetScreenText();
    }

    private static string[] GetScreenTextFromSnapshot(TerminalEmulator.TerminalCell[,] snap, int rows, int cols)
    {
        var lines = new string[rows];
        for (int r = 0; r < rows; r++)
        {
            var sb = new StringBuilder(cols);
            for (int c = 0; c < cols; c++)
            {
                var cell = snap[r, c];
                if (cell.IsWideContinuation)
                {
                    sb.Append(' ');
                    continue;
                }

                cell.AppendText(sb, replaceControlWithSpace: true);
            }

            lines[r] = sb.ToString().TrimEnd();
        }

        return lines;
    }

    /// <summary>
    /// Resize the PTY dimensions. Also resizes the emulator to keep grid in sync.
    /// </summary>
    /// <param name="source">Who asked for it; passed on to <see cref="Resized"/>.</param>
    public void Resize(int cols, int rows, TerminalIoSource source = TerminalIoSource.Unknown)
    {
        if (_queryResponder is not null)
        {
            // After a resize a bundled console host asks for the cursor position, as soon as the
            // program next touches the console, and adopts whatever it is told. The answer is read
            // under this lock, so resizing the emulator inside it means the reply can never
            // describe the grid before the resize.
            lock (_emulatorLock)
            {
                _pty.Resize(cols, rows);
                _cols = cols;
                _rows = rows;
                ResizeEmulatorWithoutRepaint(_emulator, _queryConsumer?.IsAtSequenceBoundary == true, cols, rows);
            }
        }
        else
        {
            _pty.Resize(cols, rows);
            _cols = cols;
            _rows = rows;
            lock (_emulatorLock)
                _emulator.Resize(cols, rows);
        }

        Resized?.Invoke(source);
    }

    /// <summary>
    /// Resizes the emulator of a terminal whose console host repaints nothing afterwards.
    /// </summary>
    /// <param name="atSequenceBoundary">
    /// Whether the output fed so far ends between two sequences. When a read ended inside one
    /// (<c>ESC [</c> with <c>31m</c> still to come, a window title, half a UTF-8 character),
    /// writing to the emulator would end it and the rest would be read as text, which also moves
    /// the cursor the host is told about. The scroll is skipped then: the emulator's own resize
    /// keeps the top rows, as it always has, and only the next snapshot shows the difference.
    /// </param>
    internal static void ResizeEmulatorWithoutRepaint(
        TerminalEmulator.Terminal emulator, bool atSequenceBoundary, int cols, int rows)
    {
        if (atSequenceBoundary)
            KeepCursorLineOnScreen(emulator, rows);
        emulator.Resize(cols, rows);
    }

    /// <summary>
    /// What a terminal does when it gets shorter and its cursor is below the new last row: the
    /// rows at the top scroll into history, so the line being written stays on screen. xterm.js
    /// does that in the viewer. The emulator's own resize keeps the top rows and drops the bottom
    /// ones, which nobody saw while the built-in console host repainted the whole screen after
    /// every resize. A bundled host repaints nothing, so without this the next snapshot (a
    /// reconnect, an agent reading the screen) shows the top of the old screen and no prompt.
    /// Call it before <see cref="TerminalEmulator.Terminal.Resize"/>, under the emulator lock, and
    /// only between two sequences (<see cref="ResizeEmulatorWithoutRepaint"/>).
    /// </summary>
    /// <remarks>
    /// The emulator can only be driven through its input, so this scrolls it the way a program
    /// would: margins back to the full screen (xterm.js resets them on every resize too), line
    /// feeds from the last row, then the cursor back onto its line. It leaves the alternate
    /// screen alone: that has no history, and whatever owns it repaints after a resize. These
    /// bytes go to the emulator only; no viewer and no recording sees them.
    /// </remarks>
    internal static void KeepCursorLineOnScreen(TerminalEmulator.Terminal emulator, int newRows)
    {
        var cursorRow = emulator.CursorRow;
        var overflow = cursorRow - (newRows - 1);
        if (overflow <= 0 || emulator.IsAlternateScreen)
            return;

        emulator.Write(
            $"\u001b[r\u001b[{emulator.Rows};1H{new string('\n', overflow)}\u001b[{cursorRow - overflow + 1};{emulator.CursorCol + 1}H");
    }

    internal void KillProcessTree() => _pty.KillProcessTree();

    /// <summary>The agent explicitly finished. Drain/finalize through the ordinary process-exit path.</summary>
    internal void CompleteByAgent() => CompleteAgent(1);

    /// <summary>An idle Agent uses the same bounded process-tree teardown and recording finalization.</summary>
    internal void CompleteAfterIdle() => CompleteAgent(2);

    private void CompleteAgent(int reason)
    {
        if (_disposed || HasExited || HasPtyProcessExited() || Interlocked.CompareExchange(ref _agentCompletion, reason, 0) != 0) return;
        try { _pty.KillProcessTree(); }
        catch { Volatile.Write(ref _agentCompletion, 0); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _pty.ProcessExited -= OnPtyProcessExited;

        await _cts.CancelAsync();

        try
        {
            _pty.Kill();
        }
        catch (Exception ex)
        {
            if (OperatingSystem.IsWindows())
                Log.Warning(ex, "[Terminal] Windows PTY cleanup failed");
            else
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
        // ConPTY normally releases its pending read here; a Unix non-owning synchronous
        // stream need not. Give final bytes a bounded drain, then join only output dispatch,
        // never the provider's potentially permanent read. The same lock/check in ReadLoop
        // prevents a late completion from writing into an already finalized recording.
        if (_readLoop != null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { Log.Warning("[Terminal] PTY reader remained blocked after disposal; quiescing output"); }
            catch { }
        }
        lock (_subscriberLock)
        {
            NotifyConsumersClosed();
            _outputQuiesced = true;
        }
        _cts.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var buffer = new byte[4096];

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
                    if (_outputQuiesced) break;
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
            // Disposing closes the pipe under a read that EOF never released; that is the expected
            // way out for a read loop whose process-exit path already ended the session.
            if (!_disposed)
                Log.Error(ex, "[Terminal] Read loop error");
        }
        finally
        {
            RaiseExitedOnce(ReadPtyExitCode());
        }
    }

    /// <summary>
    /// Signals end-of-stream to every consumer, so any that buffers bytes between reads can flush.
    /// Runs before <c>Exited</c> so trailing output lands before listeners tear the session down.
    /// </summary>
    private void NotifyConsumersClosed()
    {
        lock (_subscriberLock)
        {
            if (_consumersClosed) return;
            _consumersClosed = true;
            foreach (var consumer in _consumers)
            {
                try
                {
                    consumer.OnClosed();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Terminal] Consumer error while closing");
                }
            }
        }
    }

    private sealed class Unsubscriber(Terminal terminal, ITerminalConsumer consumer) : IDisposable
    {
        public void Dispose() => terminal.Unsubscribe(consumer);
    }
}
