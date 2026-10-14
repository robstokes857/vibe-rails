using System.Globalization;
using Serilog;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Host-wide pacing for reconnects to the viberails.ai relay, shared by every VibeRails process on
/// this machine. Each terminal tab is its own process with its own <see cref="RemoteTerminalConnection"/>,
/// so without a shared gate N open terminals each probe an unavailable host on their own schedule
/// (VIBE-157: four processes at attempts 32–65, each every ~30 s).
///
/// While the host is healthy this gate is a no-op. Once any process sees a host-wide failure (no
/// response, 429 or 5xx) it records when the outage began; from then on one process at a time
/// claims the next probe and the rest wait for its outcome. The wait grows with the outage's age,
/// from <see cref="FirstDelay"/> to <see cref="MaxDelay"/>, so it backs off like a doubling schedule
/// without counting how many processes reported the same failure. A success, or any HTTP answer
/// that proves the host is up, clears the outage and every waiter proceeds on its next poll.
///
/// The state is two timestamps in a small file beside state.db. This gate may slow a reconnect
/// but must never stop one: a file that cannot be used falls back to pacing this process alone,
/// and a time further out than <see cref="MaxRetryAfter"/> (clock moved back, bad file) is ignored.
/// </summary>
public sealed class RemoteRelayBackoff
{
    public const string FileName = ".remote-relay-backoff";
    private const string LockFileName = ".remote-relay-backoff.lock";

    internal static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);
    /// <summary>Longest server-requested Retry-After honored; anything later is treated as this.</summary>
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);
    /// <summary>Waiters re-read the shared file this often (local I/O only) so a success elsewhere releases them.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    /// <summary>
    /// How long a claimed probe holds off the other processes. One probe is a 10 s handshake plus
    /// at most one re-registration and second handshake; a crashed prober's claim expires after this.
    /// </summary>
    internal static readonly TimeSpan ProbeLease = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(2);
    /// <summary>After the file fails, this process paces itself from memory for this long before trying it again.</summary>
    private static readonly TimeSpan FileRetryInterval = TimeSpan.FromMinutes(1);

    private static readonly Lazy<RemoteRelayBackoff> s_shared = new(() =>
        new RemoteRelayBackoff(CrossProcessFileLock.BesideStateDatabase(ParserConfigs.GetStatePath(), FileName)));

    private readonly Lock _gate = new();
    private readonly string? _path;
    private readonly string? _lockPath;
    private readonly Func<DateTime> _clock;
    private readonly Func<double> _random;
    // The last state this process saw. The only state without a file (tests), and the fallback
    // while the file cannot be used.
    private State _memory = State.Healthy;
    private DateTime _fileRetryUtc = DateTime.MinValue;

    /// <summary>The machine-wide gate beside the state database.</summary>
    public static RemoteRelayBackoff Shared => s_shared.Value;

    /// <summary>Process memory only.</summary>
    public RemoteRelayBackoff() : this(null) { }

    internal RemoteRelayBackoff(string? path, Func<DateTime>? clock = null, Func<double>? random = null)
    {
        _path = path;
        _lockPath = path is null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, LockFileName);
        _clock = clock ?? (() => DateTime.UtcNow);
        _random = random ?? Random.Shared.NextDouble;
    }

    /// <summary>Returns at once while the host is healthy; otherwise waits until this process holds the next probe.</summary>
    public async Task WaitForTurnAsync(CancellationToken ct)
    {
        while (true)
        {
            var wait = TryClaimProbe();
            if (wait <= TimeSpan.Zero)
                return;
            await Task.Delay(wait < PollInterval ? wait : PollInterval, ct);
        }
    }

    /// <summary>
    /// Zero when the caller may contact the relay now (claiming the probe during an outage);
    /// otherwise how long until the next probe is due.
    /// </summary>
    internal TimeSpan TryClaimProbe() => Update(state =>
    {
        if (state.OutageStartedUtc is null)
            return (state, TimeSpan.Zero);
        var now = _clock();
        var wait = state.NotBeforeUtc - now;
        // Nothing this class writes is further out than MaxRetryAfter. A later time means the
        // clock moved backward or the file is bad: treat the probe as due rather than wait it out.
        if (wait > TimeSpan.Zero && wait <= MaxRetryAfter)
            return (state, wait);
        return (state with { NotBeforeUtc = now + ProbeLease }, TimeSpan.Zero);
    });

    /// <summary>
    /// The relay did not answer, or answered 429/5xx. Schedules the next probe for every process.
    /// <paramref name="retryAfter"/> is the server's Retry-After, when it sent one.
    /// <paramref name="claimedTurn"/> is true when the attempt went through
    /// <see cref="WaitForTurnAsync"/>: its outcome replaces the schedule, including its own
    /// probe claim. An attempt that skipped the gate (a new terminal's first connect) only ever
    /// pushes the next probe later, so it cannot shorten a server's Retry-After or let a second
    /// process probe while another's claim is still out.
    /// </summary>
    public void ReportHostFailure(TimeSpan? retryAfter = null, bool claimedTurn = true) => Update(state =>
    {
        var now = _clock();
        // An outage "started" in the future means the clock moved backward; restart its age.
        var started = state.OutageStartedUtc is { } recorded && recorded <= now ? recorded : now;
        var age = now - started;
        var delay = age < FirstDelay ? FirstDelay : age > MaxDelay ? MaxDelay : age;
        // ±20% so many desktops returning from the same server recycle do not arrive in step.
        delay = TimeSpan.FromSeconds(delay.TotalSeconds * (0.8 + _random() * 0.4));
        if (retryAfter is { } requested && requested > delay)
            delay = requested > MaxRetryAfter ? MaxRetryAfter : requested;
        var notBefore = now + delay;
        if (!claimedTurn && state.OutageStartedUtc is not null
            && state.NotBeforeUtc > notBefore && state.NotBeforeUtc - now <= MaxRetryAfter)
            notBefore = state.NotBeforeUtc;
        return (new State(started, notBefore), true);
    });

    /// <summary>The relay answered (connected, or a per-session status such as 404): the outage is over.</summary>
    public void ReportHostAvailable() => Update(state =>
        state.OutageStartedUtc is null ? (state, false) : (State.Healthy, true));

    /// <summary>True while an outage is recorded; for tests and diagnostics.</summary>
    internal bool IsInOutage => Update(state => (state, state.OutageStartedUtc is not null));

    private T Update<T>(Func<State, (State Next, T Result)> change)
    {
        lock (_gate)
        {
            if (_path is not null && _lockPath is not null && _clock() >= _fileRetryUtc)
            {
                try
                {
                    using var held = CrossProcessFileLock.Acquire(_lockPath, LockWait);
                    var current = Read(_path);
                    var (next, result) = change(current);
                    if (next != current)
                        AtomicFile.WriteAllText(_path, next.Serialize());
                    _memory = next;
                    return result;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
                {
                    // Pace this process on its own rather than not at all: without the file, every
                    // terminal would otherwise retry a down host about once a second. Stay on memory
                    // for a while, so a file that reads but cannot be written does not lose the outage.
                    _fileRetryUtc = _clock() + FileRetryInterval;
                    Log.Debug(ex, "[Remote] Relay backoff state file unavailable; pacing this process only");
                }
            }

            var (memoryNext, memoryResult) = change(_memory);
            _memory = memoryNext;
            return memoryResult;
        }
    }

    private static State Read(string path)
    {
        try
        {
            return File.Exists(path) ? State.Parse(AtomicFile.ReadAllText(path)) : State.Healthy;
        }
        catch (FileNotFoundException)
        {
            return State.Healthy;
        }
    }

    internal sealed record State(DateTime? OutageStartedUtc, DateTime NotBeforeUtc)
    {
        public static readonly State Healthy = new(null, DateTime.MinValue);

        public string Serialize() => OutageStartedUtc is { } started
            ? string.Create(CultureInfo.InvariantCulture, $"{started.Ticks} {NotBeforeUtc.Ticks}")
            : "";

        /// <summary>Anything unreadable is a healthy host: a corrupt file must not hold reconnects back.</summary>
        public static State Parse(string text)
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var started)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var notBefore)
                || started > DateTime.MaxValue.Ticks || notBefore > DateTime.MaxValue.Ticks)
                return Healthy;
            return new(new DateTime(started, DateTimeKind.Utc), new DateTime(notBefore, DateTimeKind.Utc));
        }
    }
}
