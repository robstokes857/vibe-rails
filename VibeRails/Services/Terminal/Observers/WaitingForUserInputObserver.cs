using System.Collections.Concurrent;
using System.Text;
using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.Terminal;

public sealed class WaitingForUserInputObserver : ITerminalIoObserver
{
    private const int BufferCapacity = 16384;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WorkingSignalGracePeriod = TimeSpan.FromSeconds(45);

    private readonly IAppEventBus _eventBus;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SessionBuffer> _buffers = new();
    private readonly ConcurrentDictionary<string, byte> _codexSessions = new();

    public WaitingForUserInputObserver(IAppEventBus eventBus)
        : this(eventBus, TimeProvider.System)
    {
    }

    public WaitingForUserInputObserver(IAppEventBus eventBus, TimeProvider timeProvider)
    {
        _eventBus = eventBus;
        _timeProvider = timeProvider;
    }

    public ValueTask OnSessionStartAsync(TerminalSessionStartEvent startEvent, CancellationToken cancellationToken = default)
    {
        if (string.Equals(startEvent.Cli, "codex", StringComparison.OrdinalIgnoreCase))
            _codexSessions[startEvent.SessionId] = 0;
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTerminalIoAsync(TerminalIoEvent ioEvent, CancellationToken cancellationToken = default)
    {
        if (ioEvent.Direction != TerminalIoDirection.Output)
            return ValueTask.CompletedTask;

        if (!_codexSessions.ContainsKey(ioEvent.SessionId))
            return ValueTask.CompletedTask;

        var buffer = _buffers.GetOrAdd(ioEvent.SessionId, _ => new SessionBuffer(_timeProvider));

        if (buffer.AppendAndCheck(ioEvent.PlainText))
        {
            _eventBus.Publish(
                "session_waiting_for_user",
                new SessionWaitingForUserPayload(ioEvent.SessionId),
                AppJsonSerializerContext.Default.SessionWaitingForUserPayload);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask OnSessionCompleteAsync(TerminalSessionCompleteEvent completeEvent, CancellationToken cancellationToken = default)
    {
        _buffers.TryRemove(completeEvent.SessionId, out _);
        _codexSessions.TryRemove(completeEvent.SessionId, out _);
        return ValueTask.CompletedTask;
    }

    private sealed class SessionBuffer
    {
        private readonly StringBuilder _window = new(BufferCapacity);
        private readonly TimeProvider _timeProvider;
        private readonly Lock _lock = new();
        private DateTimeOffset _nextCheckUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _suppressWaitingUntilUtc = DateTimeOffset.MinValue;

        public SessionBuffer(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        public bool AppendAndCheck(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            lock (_lock)
            {
                var now = _timeProvider.GetUtcNow();

                // Per-event grace-period set: scan only the incoming chunk. PTY
                // chunks are typically large enough to contain a full "Working
                // (Xs • esc to interrupt)" line, and the partial-match patterns
                // ("orking", "king (") catch the common chunk-boundary cuts.
                // The 15s window check below also re-scans the rolling buffer
                // for whole signals, so this is a hot-path optimization, not
                // the only line of defense.
                if (ContainsCodexWorkingSignal(text))
                    _suppressWaitingUntilUtc = now + WorkingSignalGracePeriod;

                _window.Append(text);
                if (_window.Length > BufferCapacity)
                    _window.Remove(0, _window.Length - BufferCapacity);

                if (now < _nextCheckUtc)
                    return false;

                _nextCheckUtc = now + CheckInterval;

                var snapshot = _window.ToString();
                _window.Clear();

                if (!ContainsCodexWaitingGlyphPair(snapshot))
                    return false;

                if (ContainsCodexWorkingSignal(snapshot))
                    return false;

                if (now < _suppressWaitingUntilUtc && LooksLikeCodexProgressContinuation(snapshot))
                    return false;

                return true;
            }
        }

        private static bool ContainsCodexWaitingGlyphPair(string text)
        {
            return text.Contains('•') && text.Contains('◦');
        }

        private static bool ContainsCodexWorkingSignal(string text)
        {
            // "orking" subsumes "working"/"Working"/"WORKING" since each contains
            // "orking" as a substring. "king (" catches the chunk-boundary cut
            // where "...Wor" landed on the previous chunk.
            return text.Contains("orking", StringComparison.OrdinalIgnoreCase)
                || text.Contains("king (", StringComparison.OrdinalIgnoreCase)
                || (ContainsTimerFragment(text)
                    && text.Contains("esc to interrupt", StringComparison.OrdinalIgnoreCase));
        }

        private static bool LooksLikeCodexProgressContinuation(string text)
        {
            if (ContainsCodexWorkingSignal(text))
                return true;

            var hasProgressGlyph = false;
            foreach (var ch in text)
            {
                if (ch is '•' or '◦' or '·' or '*' or '✢' or '✶' or '✻' or '✽')
                {
                    hasProgressGlyph = true;
                    continue;
                }

                if (char.IsWhiteSpace(ch) || char.IsDigit(ch))
                    continue;

                if (ch is '(' or ')' or '[' or ']' or ':' or ';' or ',' or '.' or '/' or '\\' or '-' or '+')
                    continue;

                if (ch is 's' or 'S' or 'm' or 'M' or 'h' or 'H')
                    continue;

                return false;
            }

            return hasProgressGlyph;
        }

        private static bool ContainsTimerFragment(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '(')
                    continue;

                var pos = i + 1;
                while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                    pos++;

                if (!ReadNumber(text, ref pos))
                    continue;

                if (pos >= text.Length)
                    continue;

                if (text[pos] is 's' or 'S')
                    return true;

                if (text[pos] is not ('m' or 'M' or 'h' or 'H'))
                    continue;

                pos++;
                if (pos >= text.Length || text[pos] is ')' or ' ' or '•' or ',' or ';')
                    return true;

                while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                    pos++;

                if (ReadNumber(text, ref pos) && pos < text.Length && text[pos] is 's' or 'S' or 'm' or 'M')
                    return true;
            }

            return false;
        }

        private static bool ReadNumber(string text, ref int pos)
        {
            var start = pos;
            while (pos < text.Length && char.IsDigit(text[pos]))
                pos++;

            return pos > start;
        }
    }
}
