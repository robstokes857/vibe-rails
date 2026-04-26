using System.Collections.Concurrent;
using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Detects when codex is sitting at a prompt waiting for the user.
///
/// Approach: watch the last 5 seconds of raw PTY chunks. Codex's idle screen
/// keeps repainting the SAME tiny ANSI cursor-positioning pattern every frame
/// to keep the spinner alive — so the buffer ends up dominated by many copies
/// of one or two distinct chunks. Codex's "Working" state, in contrast, patches
/// a different character or cursor position on every frame, producing dozens
/// of distinct chunks per 5-second window. The two states are cleanly
/// separated by counting distinct chunk byte content.
///
/// We fire once per idle→busy→idle cycle: the buffer is re-evaluated on every
/// PTY chunk; as soon as it stops looking idle (codex started doing something
/// again) the gate is cleared, so the next time codex settles we fire again.
/// </summary>
public sealed class WaitingForUserInputObserver : ITerminalIoObserver
{
    private static readonly TimeSpan SampleWindow = TimeSpan.FromSeconds(5);
    private const int IdleChunkSizeThreshold = 50;
    private const int IdleMaxUniqueBigChunks = 5;
    private const int IdleMinTopChunkCount = 20;
    private const int IdleMinBigChunkSamples = 40;
    private const int QuietBufferThreshold = 5;
    private const int MaxQueuedChunks = 20_000;

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

        if (buffer.AppendAndCheck(ioEvent.Text))
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
        private readonly TimeProvider _timeProvider;
        private readonly Lock _lock = new();
        private readonly Queue<TimedChunk> _chunks = new();
        private bool _hasFired;

        public SessionBuffer(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        public bool AppendAndCheck(string rawText)
        {
            if (string.IsNullOrEmpty(rawText))
                return false;

            lock (_lock)
            {
                var now = _timeProvider.GetUtcNow();
                _chunks.Enqueue(new TimedChunk(now, rawText));

                // Hard cap on queue size in case a noisy session pumps a huge
                // burst of small chunks faster than the time-cutoff drains them.
                while (_chunks.Count > MaxQueuedChunks)
                    _chunks.Dequeue();

                var cutoff = now - SampleWindow;
                while (_chunks.Count > 0 && _chunks.Peek().Timestamp < cutoff)
                    _chunks.Dequeue();

                switch (Classify(_chunks))
                {
                    case BufferVerdict.Working:
                        // Codex is animating diverse frames — release the
                        // once-per-cycle gate so the next time it settles we
                        // fire again.
                        _hasFired = false;
                        return false;

                    case BufferVerdict.Idle:
                        if (_hasFired)
                            return false;
                        _hasFired = true;
                        return true;

                    default:
                        // Indeterminate (sparse window or weak repetition).
                        // Don't touch the gate — silent gaps in a real idle
                        // (user reading the screen) shouldn't cause a duplicate
                        // fire when traffic resumes.
                        return false;
                }
            }
        }

        private enum BufferVerdict { Indeterminate, Working, Idle }

        private static BufferVerdict Classify(IEnumerable<TimedChunk> chunks)
        {
            Dictionary<string, int>? bigCounts = null;
            var bigTotal = 0;
            var totalChunks = 0;
            foreach (var c in chunks)
            {
                totalChunks++;
                if (c.Content.Length < IdleChunkSizeThreshold)
                    continue;
                bigCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
                bigCounts[c.Content] = bigCounts.GetValueOrDefault(c.Content) + 1;
                bigTotal++;
            }

            // Buffer is effectively silent (user paused while reading the
            // screen). Don't disturb the gate — the next chunk arriving
            // shouldn't re-fire on the same logical idle period.
            if (totalChunks < QuietBufferThreshold)
                return BufferVerdict.Indeterminate;

            // Buffer has plenty of chunks but none look like the idle pattern
            // (codex per-char working animation, or arbitrary CLI chatter).
            // Treat as Working so the gate is released for the next true idle.
            if (bigCounts is null || bigTotal < IdleMinBigChunkSamples)
                return BufferVerdict.Working;

            if (bigCounts.Count > IdleMaxUniqueBigChunks)
                return BufferVerdict.Working;

            var topCount = 0;
            foreach (var v in bigCounts.Values)
                if (v > topCount) topCount = v;

            return topCount >= IdleMinTopChunkCount
                ? BufferVerdict.Idle
                : BufferVerdict.Working;
        }

        private readonly record struct TimedChunk(DateTimeOffset Timestamp, string Content);
    }
}
