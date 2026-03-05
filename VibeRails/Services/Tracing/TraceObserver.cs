using VibeRails.Services.Terminal;

namespace VibeRails.Services.Tracing;

/// <summary>
/// Terminal I/O observer that forwards all terminal events to the trace buffer.
/// Registered alongside MyTerminalObserver — both fire via TerminalIoObserverService.
/// </summary>
public sealed class TraceObserver : ITerminalIoObserver
{
    private readonly TraceEventBuffer _buffer;

    public TraceObserver(TraceEventBuffer buffer)
    {
        _buffer = buffer;
    }

    public ValueTask OnTerminalIoAsync(TerminalIoEvent ioEvent, CancellationToken cancellationToken = default)
    {
        if (ioEvent.Direction == TerminalIoDirection.Input)
        {
            var plain = ioEvent.PlainText;
            if (string.IsNullOrWhiteSpace(plain))
                return ValueTask.CompletedTask;

            _buffer.Add(TraceEvent.Create(
                TraceEventType.TerminalInput,
                $"Terminal.{ioEvent.Source}",
                $"Input ({ioEvent.Source}): {Truncate(plain, 120)}",
                plain));
        }
        else
        {
            var plain = ioEvent.PlainText;
            if (string.IsNullOrWhiteSpace(plain))
                return ValueTask.CompletedTask;

            // Store raw ANSI text in Detail so the trace viewer can render it in xterm.js
            // without connecting to the live terminal WebSocket (which would steal the session).
            _buffer.Add(TraceEvent.Create(
                TraceEventType.TerminalOutput,
                "Terminal.Pty",
                $"Output: {Truncate(plain, 200)}",
                ioEvent.Text));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnTerminalResizeAsync(TerminalResizeEvent resizeEvent, CancellationToken cancellationToken = default)
    {
        _buffer.Add(TraceEvent.Create(
            TraceEventType.Resize,
            $"Terminal.{resizeEvent.Source}",
            $"Resize: {resizeEvent.Cols}x{resizeEvent.Rows}",
            $"sessionId: {resizeEvent.SessionId}\nsource: {resizeEvent.Source}\ncols: {resizeEvent.Cols}\nrows: {resizeEvent.Rows}"));

        return ValueTask.CompletedTask;
    }

    public ValueTask OnTerminalIdleAsync(TerminalIdleEvent idleEvent, CancellationToken cancellationToken = default)
    {
        var detail = $"sessionId: {idleEvent.SessionId}\n" +
                     $"idleFor: {idleEvent.IdleFor.TotalSeconds:F1}s\n" +
                     $"threshold: {idleEvent.IdleThreshold.TotalSeconds:F0}s\n" +
                     $"lastInput: {idleEvent.LastInputUtc:HH:mm:ss.fff}\n" +
                     $"lastOutput: {idleEvent.LastOutputUtc:HH:mm:ss.fff}";

        _buffer.Add(TraceEvent.Create(
            TraceEventType.Idle,
            "Terminal",
            $"Idle for {idleEvent.IdleFor.TotalSeconds:F0}s (waiting for input)",
            detail));

        return ValueTask.CompletedTask;
    }

    public ValueTask OnTerminalRemoteCommandAsync(TerminalRemoteCommandEvent commandEvent, CancellationToken cancellationToken = default)
    {
        var detail = $"sessionId: {commandEvent.SessionId}\n" +
                     $"source: {commandEvent.Source}\n" +
                     $"command: {commandEvent.Command}\n" +
                     $"payload: {commandEvent.Payload ?? "(none)"}";

        _buffer.Add(TraceEvent.Create(
            TraceEventType.SessionLifecycle,
            $"Terminal.{commandEvent.Source}",
            $"Remote command: {commandEvent.Command}",
            detail));

        return ValueTask.CompletedTask;
    }

    private static string Truncate(string text, int maxLength)
    {
        // Collapse whitespace for summary display
        var collapsed = System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
        return collapsed.Length <= maxLength ? collapsed : collapsed[..maxLength] + "...";
    }
}
