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
        if (status is not { HasActiveSession: false })
            return null;

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
