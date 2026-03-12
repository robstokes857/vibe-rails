using VibeRails.Services.Terminal;
using System.Text;

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
        var raw = ioEvent.Text;
        if (string.IsNullOrEmpty(raw))
            return ValueTask.CompletedTask;

        if (ioEvent.Direction == TerminalIoDirection.Input)
        {
            _buffer.Add(TraceEvent.Create(
                TraceEventType.TerminalInput,
                $"Terminal.{ioEvent.Source}",
                $"Input ({ioEvent.Source}): {DescribeForSummary(raw, 160)}",
                raw));
        }
        else
        {
            // Store raw ANSI text in Detail so the trace viewer can render it in xterm.js
            // without connecting to the live terminal WebSocket (which would steal the session).
            _buffer.Add(TraceEvent.Create(
                TraceEventType.TerminalOutput,
                $"Terminal.{ioEvent.Source}",
                $"Output ({ioEvent.Source}): {DescribeForSummary(raw, 200)}",
                raw));
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

    private static string DescribeForSummary(string text, int maxLength)
    {
        var sb = new StringBuilder(Math.Min(text.Length, maxLength));

        foreach (var ch in text)
        {
            var token = ch switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\b' => "\\b",
                '\x1b' => "\\x1b",
                '\x7f' => "\\x7f",
                _ when char.IsControl(ch) => $"\\x{(int)ch:x2}",
                _ => ch.ToString()
            };

            if (sb.Length + token.Length > maxLength)
            {
                sb.Append("...");
                break;
            }

            sb.Append(token);
        }

        return sb.Length == 0 ? "(empty)" : sb.ToString();
    }
}
