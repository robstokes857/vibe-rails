using System.Collections.Concurrent;
using Serilog;

namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// Tracks raw PTY output for Automation terminals and requests shutdown when one has produced no
/// bytes for <see cref="IdleTimeout"/>. The singleton owns one timer shared by every session registered
/// in this process; ordinary terminals never register and therefore never start the timer.
/// </summary>
public sealed class AutomationConsumer : IAutomationConsumer, IDisposable
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    // A short scan keeps the enforced timeout close to two minutes without re-arming a timer from
    // OnOutput, which runs synchronously on the PTY read loop and must remain very cheap.
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<string, SessionActivity> _sessions = new();
    private readonly CancellationTokenSource _idleShutdown = new();
    private readonly TimeProvider _timeProvider;
    private readonly object _timerGate = new();

    private Timer? _timer;
    private string? _idleSessionId;
    private int _idleShutdownRequested;
    private int _disposed;

    public AutomationConsumer()
        : this(TimeProvider.System)
    {
    }

    internal AutomationConsumer(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public CancellationToken IdleShutdownToken => _idleShutdown.Token;

    public bool IdleShutdownRequested => Volatile.Read(ref _idleShutdownRequested) != 0;

    public string? IdleSessionId => Volatile.Read(ref _idleSessionId);

    internal int ActiveSessionCount => _sessions.Count;

    internal bool TimerStarted => Volatile.Read(ref _timer) is not null;

    /// <summary>
    /// Registers an Automation terminal and returns the lightweight consumer that must be attached
    /// before its PTY read loop starts. Registration itself counts as the initial activity, so a
    /// terminal that never emits a byte is still closed after two minutes.
    /// </summary>
    public ITerminalConsumer RegisterSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var activity = new SessionActivity(_timeProvider.GetTimestamp());
        if (!_sessions.TryAdd(sessionId, activity))
            throw new InvalidOperationException($"Automation session '{sessionId}' is already registered.");

        try
        {
            EnsureTimerStarted();
            return new SessionOutputConsumer(this, sessionId, activity);
        }
        catch
        {
            RemoveSession(sessionId, activity);
            throw;
        }
    }

    private void EnsureTimerStarted()
    {
        if (Volatile.Read(ref _timer) is not null)
            return;

        lock (_timerGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _timer ??= new Timer(
                static state => ((AutomationConsumer)state!).ScanForIdleSessionsSafely(),
                this,
                ScanInterval,
                ScanInterval);
        }
    }

    private void RecordOutput(SessionActivity activity, ReadOnlyMemory<byte> data)
    {
        // PTY reads never dispatch empty chunks, but treating an empty synthetic call as activity
        // would make the timeout depend on something that was not actually output.
        if (data.IsEmpty)
            return;

        lock (activity.Gate)
        {
            if (activity.Closed || activity.Idle)
                return;

            activity.LastOutputTimestamp = _timeProvider.GetTimestamp();
        }
    }

    private void ScanForIdleSessionsSafely()
    {
        try
        {
            ScanForIdleSessions();
        }
        catch (Exception ex)
        {
            // A timer callback must never bring down the Job host. The next tick can retry.
            Log.Error(ex, "[Jobs] Automation idle scan failed");
        }
    }

    internal void ScanForIdleSessions()
    {
        if (Volatile.Read(ref _disposed) != 0 || IdleShutdownRequested)
            return;

        var now = _timeProvider.GetTimestamp();
        string? firstIdleSession = null;

        foreach (var pair in _sessions)
        {
            var activity = pair.Value;
            lock (activity.Gate)
            {
                if (activity.Closed || activity.Idle)
                    continue;

                if (_timeProvider.GetElapsedTime(activity.LastOutputTimestamp, now) < IdleTimeout)
                    continue;

                // Output and expiry contend on the same lock. At the exact boundary, whichever
                // obtains it first wins: output resets the full window, otherwise shutdown begins.
                activity.Idle = true;
                firstIdleSession ??= pair.Key;
            }
        }

        if (firstIdleSession is null
            || Interlocked.CompareExchange(ref _idleShutdownRequested, 1, 0) != 0)
        {
            return;
        }

        Interlocked.CompareExchange(ref _idleSessionId, firstIdleSession, null);
        Log.Information(
            "[Jobs] Automation session {SessionId} produced no PTY output for {IdleSeconds} seconds; requesting Job host shutdown",
            firstIdleSession,
            IdleTimeout.TotalSeconds);

        try
        {
            _idleShutdown.Cancel(throwOnFirstException: false);
        }
        catch (AggregateException ex)
        {
            // Cancellation has already been recorded and remains requested. A misbehaving token
            // callback must not prevent the JobRunner's other callbacks from seeing the signal.
            Log.Warning(ex, "[Jobs] An Automation idle-shutdown callback failed");
        }
        catch (ObjectDisposedException)
        {
            // The host raced normal disposal after the scan selected an idle session.
        }
    }

    private void RemoveSession(string sessionId, SessionActivity activity)
    {
        lock (activity.Gate)
        {
            activity.Closed = true;
        }

        // Conditional removal prevents a stale consumer from removing a future registration that
        // happens to reuse the same id.
        ((ICollection<KeyValuePair<string, SessionActivity>>)_sessions)
            .Remove(new KeyValuePair<string, SessionActivity>(sessionId, activity));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Timer? timer;
        lock (_timerGate)
        {
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();

        foreach (var activity in _sessions.Values)
        {
            lock (activity.Gate)
            {
                activity.Closed = true;
            }
        }
        _sessions.Clear();
        _idleShutdown.Dispose();
    }

    private sealed class SessionActivity(long registeredTimestamp)
    {
        public object Gate { get; } = new();
        public long LastOutputTimestamp { get; set; } = registeredTimestamp;
        public bool Closed { get; set; }
        public bool Idle { get; set; }
    }

    private sealed class SessionOutputConsumer(
        AutomationConsumer owner,
        string sessionId,
        SessionActivity activity) : ITerminalConsumer
    {
        public void OnOutput(ReadOnlyMemory<byte> data) => owner.RecordOutput(activity, data);

        public void OnClosed() => owner.RemoveSession(sessionId, activity);
    }
}
