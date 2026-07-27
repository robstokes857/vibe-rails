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
    // Exposed (internal) so the unit tests can size their loops against the
    // observer's rolling window without duplicating the value. Bumping this
    // does not require touching test iteration counts as a result.
    internal static readonly TimeSpan SampleWindow = TimeSpan.FromSeconds(5);
    private const int IdleChunkSizeThreshold = 50;
    private const int IdleMaxUniqueBigChunks = 5;
    // Codex's idle keepalive emits ~1 big chunk per second (a CUP+EL row-clear
    // sweep, ~150 bytes), bracketed by tiny sub-threshold chunks, and rotates
    // through a 2- or 3-frame spinner. A 5-second window therefore lands at
    // bigTotal=5, unique=2–3, topCount=3–4. Older codex builds emitted many
    // more frames per second; the previous {Top=20, Samples=40} gates assumed
    // that density and are unreachable on the current cadence. The structural
    // defense against working-state false-positives is IdleMaxUniqueBigChunks:
    // codex's per-char working animation hits unique=24–80 in any 5s window
    // (verified against Session_881cd29d fixture), so idle and working remain
    // cleanly separated by unique-count alone. TopChunkCount=2 keeps the
    // "4 distinct big chunks, all unique" transient working pattern out.
    private const int IdleMinTopChunkCount = 2;
    private const int IdleMinBigChunkSamples = 4;
    private const int QuietBufferThreshold = 5;
    private const int MaxQueuedChunks = 20_000;
    // Small-chunk concentration gate. The big-chunk uniqueness gate alone
    // can't distinguish a codex Working sub-phase that streams bit-identical
    // ~124-byte CUP+EL "cursor-park" frames (big_uniq=1, big_top high — looks
    // exactly like idle keepalive on the big-chunk axis) from a true idle.
    // The difference shows up on the *small* chunks: a real idle brackets
    // each big keepalive with a small repeating set (mode-toggle, cursor-
    // restore, fixed-position CUP), so a few small chunks repeat heavily —
    // small_top stays at or above the count of distinct small chunks. A
    // working cursor-park phase emits varying cursor-position chunks (each
    // appearing only a few times), so small_unique grows faster than any
    // one of them repeats. We reject Idle when small_top < small_unique
    // *and* the small-chunk sample is large enough to be meaningful.
    //
    // Concrete numbers at the fire moment (from analyze_fixture_buffer.py):
    //   e910eb94 working falsefire: small_top=11 vs small_unique=12 → REJECT
    //   881cd29d approval menu:     small_top=49 vs small_unique=13 → ACCEPT
    //   0ebd404d idle-at-end:       small_top=50 vs small_unique=12 → ACCEPT
    //   e910eb94 composer truefire: small_top=65 vs small_unique=60 → ACCEPT
    //
    // The e910eb94 composer case is the tight one — only 5 chunks of margin.
    // Ratio-based gates (small_top / small_total) do NOT discriminate here:
    // both populations sit around 33–35%, because the working pattern packs
    // a small total into a few seconds while the idle pattern packs a much
    // larger total into the same window. The diversity comparison is the
    // signal — keep it. If a future codex update tightens the composer
    // margin further or shifts small_unique past current bounds, add a new
    // fixture and re-tune rather than going to a ratio.
    private const int SmallChunkConcentrationMinSamples = 6;
    // The buffer must have been collecting for nearly a full SampleWindow before
    // we classify. Codex's per-char working animation can hit a transient
    // (unique=4, topCount=4) shape during the first ~250ms of the buffer
    // filling — diverse frames haven't arrived yet, so they look like idle.
    // Requiring the oldest chunk to be at least IdleMinObservationSpan old
    // gives the unique-count discriminator (which is the real working signal)
    // time to engage. The old IdleMinBigChunkSamples=40 happened to mask this
    // by being unreachable in <1s; making the time guard explicit is clearer
    // and survives codex tightening or relaxing its frame cadence.
    private static readonly TimeSpan IdleMinObservationSpan = TimeSpan.FromSeconds(2);

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
        if (!_codexSessions.ContainsKey(ioEvent.SessionId))
            return ValueTask.CompletedTask;

        // A submit is a hard boundary between two possible waiting periods.
        // Discard the pre-submit rolling window so Codex's old idle repaint
        // frames cannot immediately produce another waiting event and visually
        // undo the tab's first Enter. The next genuine wait is still detected
        // after a fresh observation window has accumulated.
        if (ioEvent.Direction == TerminalIoDirection.Input)
        {
            if (ioEvent.Text is "\r" or "\n" or "\r\n" &&
                _buffers.TryGetValue(ioEvent.SessionId, out var existingBuffer))
            {
                existingBuffer.ResetAfterSubmit();
            }
            return ValueTask.CompletedTask;
        }

        if (ioEvent.Direction != TerminalIoDirection.Output)
            return ValueTask.CompletedTask;

        var buffer = _buffers.GetOrAdd(ioEvent.SessionId, _ => new SessionBuffer(_timeProvider));

        // Input and output can arrive on different threads. Keep the generation check and publish
        // inside one buffer-lock boundary so ResetAfterSubmit cannot invalidate the decision in
        // the gap between them and let a stale WAITING event land after Enter.
        if (buffer.AppendAndCheck(ioEvent.Text, out var generation))
        {
            buffer.PublishIfCurrentGeneration(generation, () =>
                _eventBus.Publish(
                    "session_waiting_for_user",
                    new SessionWaitingForUserPayload(ioEvent.SessionId),
                    AppJsonSerializerContext.Default.SessionWaitingForUserPayload));
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
        // Bumped by ResetAfterSubmit. A fire decision carries the generation it was made under;
        // PublishIfCurrentGeneration validates and publishes while still excluding a submit.
        private long _generation;

        public SessionBuffer(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        public bool AppendAndCheck(string rawText, out long generation)
        {
            generation = 0;
            if (string.IsNullOrEmpty(rawText))
                return false;

            lock (_lock)
            {
                generation = _generation;
                var now = _timeProvider.GetUtcNow();
                _chunks.Enqueue(new TimedChunk(now, rawText));

                // Hard cap on queue size in case a noisy session pumps a huge
                // burst of small chunks faster than the time-cutoff drains them.
                while (_chunks.Count > MaxQueuedChunks)
                    _chunks.Dequeue();

                var cutoff = now - SampleWindow;
                while (_chunks.Count > 0 && _chunks.Peek().Timestamp < cutoff)
                    _chunks.Dequeue();

                // Don't classify until the buffer has been observing for long
                // enough that the unique-count discriminator is meaningful.
                // See IdleMinObservationSpan for why.
                var oldest = _chunks.Count > 0 ? _chunks.Peek().Timestamp : now;
                if (now - oldest < IdleMinObservationSpan)
                    return false;

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

        public void ResetAfterSubmit()
        {
            lock (_lock)
            {
                _chunks.Clear();
                _hasFired = false;
                _generation++;
            }
        }

        public void PublishIfCurrentGeneration(long generation, Action publish)
        {
            lock (_lock)
            {
                if (_generation == generation)
                    publish();
            }
        }

        private enum BufferVerdict { Indeterminate, Working, Idle }

        // Concrete Queue<TimedChunk> parameter (not IEnumerable<>) so the
        // struct enumerator stays on the stack — this runs once per PTY
        // chunk, so even a single boxed-enumerator allocation per call is
        // a persistent micro-allocation in a hot path.
        private static BufferVerdict Classify(Queue<TimedChunk> chunks)
        {
            Dictionary<string, int>? bigCounts = null;
            Dictionary<string, int>? smallCounts = null;
            var bigTotal = 0;
            var smallTotal = 0;
            var totalChunks = 0;
            foreach (var c in chunks)
            {
                totalChunks++;
                if (c.Content.Length >= IdleChunkSizeThreshold)
                {
                    bigCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
                    bigCounts[c.Content] = bigCounts.GetValueOrDefault(c.Content) + 1;
                    bigTotal++;
                }
                else
                {
                    smallCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
                    smallCounts[c.Content] = smallCounts.GetValueOrDefault(c.Content) + 1;
                    smallTotal++;
                }
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

            if (topCount < IdleMinTopChunkCount)
                return BufferVerdict.Working;

            // Small-chunk concentration gate — see the constant comment for
            // the rationale. Only applied when we have enough small samples
            // to make the comparison meaningful; below that, fall back to
            // the big-chunk verdict only.
            if (smallCounts is not null && smallTotal >= SmallChunkConcentrationMinSamples)
            {
                var smallTop = 0;
                foreach (var v in smallCounts.Values)
                    if (v > smallTop) smallTop = v;
                if (smallTop < smallCounts.Count)
                    return BufferVerdict.Working;
            }

            return BufferVerdict.Idle;
        }

        private readonly record struct TimedChunk(DateTimeOffset Timestamp, string Content);
    }
}
