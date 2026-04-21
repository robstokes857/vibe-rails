using System.Collections.Concurrent;
using System.Text;
using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.Terminal;

public sealed class WaitingForUserInputObserver : ITerminalIoObserver
{
    private const int BufferCapacity = 16384;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly IAppEventBus _eventBus;
    private readonly ConcurrentDictionary<string, SessionBuffer> _buffers = new();
    private readonly ConcurrentDictionary<string, byte> _codexSessions = new();

    public WaitingForUserInputObserver(IAppEventBus eventBus)
    {
        _eventBus = eventBus;
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

        var buffer = _buffers.GetOrAdd(ioEvent.SessionId, static _ => new SessionBuffer());

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
        private readonly Lock _lock = new();
        private DateTime _nextCheckUtc = DateTime.MinValue;

        public bool AppendAndCheck(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            lock (_lock)
            {
                _window.Append(text);
                if (_window.Length > BufferCapacity)
                    _window.Remove(0, _window.Length - BufferCapacity);

                var now = DateTime.UtcNow;
                if (now < _nextCheckUtc)
                    return false;

                _nextCheckUtc = now + CheckInterval;

                var snapshot = _window.ToString();
                _window.Clear();

                return snapshot.Contains('•')
                    && snapshot.Contains('◦')
                    && !snapshot.Contains("orking", StringComparison.Ordinal);
            }
        }
    }
}
