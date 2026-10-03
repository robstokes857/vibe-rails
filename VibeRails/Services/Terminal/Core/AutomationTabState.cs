using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Server-owned Automation identity and a reservation around session starts. A completed
/// host may be reclaimed only after both the run and its PTY have finished, and only if
/// no session start raced those checks. Saved recording metadata survives PTY teardown.
/// </summary>
internal sealed class AutomationTabState(string runId, string name)
{
    private readonly Lock _gate = new();
    private int _startsInFlight;
    private long _startVersion;
    private bool _reclaiming;
    private bool _hasUnconfirmedStart;
    private TerminalStatusResponse? _lastSession;

    public string RunId { get; } = runId;
    public string Name { get; } = name;

    public TerminalStatusResponse? LastSession
    {
        get { lock (_gate) return _lastSession; }
    }

    public void BeginSessionStart()
    {
        lock (_gate)
        {
            if (_reclaiming)
                throw new InvalidOperationException("This completed Automation terminal has been closed. Start a new terminal instead.");
            _startVersion++;
            _startsInFlight++;
        }
    }

    public void EndSessionStart(TerminalStatusResponse? session)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(session?.SessionId))
                _lastSession = session;
            else
                // A cancelled/failed HTTP start can still have reached the child. Preserve its
                // last recording for replay, but never kill this host based on that old session.
                _hasUnconfirmedStart = true;
            _startsInFlight--;
        }
    }

    public async Task<bool> TryReserveForReclamationAsync(
        IJobStore store,
        ISessionStore sessions,
        Func<CancellationToken, Task<TerminalStatusResponse?>> readStatus,
        CancellationToken cancellationToken)
    {
        var version = await CheckReclamationAsync(store, sessions, readStatus, cancellationToken);
        return version is { } value && TryReserveForReclamation(value);
    }

    /// <summary>
    /// How long a finished run's outer wrapper session may outlive the run row before the host is
    /// closed anyway. The wrapper normally exits within seconds of <c>vb --job-run</c> returning;
    /// this only has to clear an ordinary slow host shutdown, not a healthy workflow.
    /// </summary>
    internal static readonly TimeSpan LingeringSessionGrace = TimeSpan.FromSeconds(60);

    public async Task<long?> CheckReclamationAsync(
        IJobStore store,
        ISessionStore sessions,
        Func<CancellationToken, Task<TerminalStatusResponse?>> readStatus,
        CancellationToken cancellationToken)
    {
        long version;
        string sessionId;
        lock (_gate)
        {
            if (_reclaiming || _hasUnconfirmedStart || _startsInFlight != 0 || string.IsNullOrWhiteSpace(_lastSession?.SessionId))
                return null;
            version = _startVersion;
            sessionId = _lastSession.SessionId;
        }

        var run = await store.GetRunAsync(RunId, cancellationToken);
        if (run?.Status is not (JobRunStatus.Succeeded or JobRunStatus.Failed or JobRunStatus.Cancelled
                or JobRunStatus.TimedOut or JobRunStatus.Interrupted)
            || !string.Equals(run.TerminalSessionId, sessionId, StringComparison.Ordinal))
            return null;

        // Failed/unavailable status must never look like an idle host eligible for removal.
        var status = await readStatus(cancellationToken);
        if (status is null)
            return null;

        if (status.HasActiveSession)
        {
            // The run row is final, so the workflow process has finished its bookkeeping, yet the
            // host's PTY still reports the run's own outer session: the wrapper shell never reached
            // its `exit`, or an orphaned descendant keeps the console open. Nothing more can come
            // out of it, but while it lives the Automation menu truthfully says Running and the
            // agent never leaves the group (VIBE-45). After a grace window for an ordinary slow
            // shutdown, close the host anyway; DeleteTabAsync stops the session first, which
            // finalizes its recording, then tears down the whole child process tree.
            if (!string.Equals(status.SessionId, sessionId, StringComparison.Ordinal))
                return null;
            if (run.EndedUtc is not { } ended || DateTime.UtcNow - ended < LingeringSessionGrace)
                return null;
            return version;
        }

        // Inactive PTY state precedes output-writer disposal. Wait for the recorded session
        // to finish too, so reclaiming a host cannot interrupt its final replay-log flush.
        var recording = await sessions.GetSessionByIdAsync(sessionId, cancellationToken);
        if (recording?.EndedUTC is null)
            return null;

        return version;
    }

    public bool TryReserveForReclamation(long checkedVersion)
    {
        lock (_gate)
        {
            if (_reclaiming || _hasUnconfirmedStart || _startsInFlight != 0 || _startVersion != checkedVersion)
                return false;
            _reclaiming = true;
            return true;
        }
    }
}
