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
/// The state is two timestamps in a small file beside state.db. Every file problem fails open:
/// this gate may slow a reconnect but must never stop one.
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

    private static readonly Lazy<RemoteRelayBackoff> s_shared = new(() =>
        new RemoteRelayBackoff(CrossProcessFileLock.BesideStateDatabase(ParserConfigs.GetStatePath(), FileName)));

    private readonly Lock _gate = new();
    private readonly string? _path;
    private readonly string? _lockPath;
    private readonly Func<DateTime> _clock;
    private readonly Func<double> _random;
    // Used when there is no file (tests, or a file that cannot be written).
    private State _memory = State.Healthy;

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
        if (now < state.NotBeforeUtc)
        {
            // A clock change or a bad file can put the next probe absurdly far out; never wait past the cap.
            var wait = state.NotBeforeUtc - now;
            return (state, wait > MaxRetryAfter ? MaxRetryAfter : wait);
        }
        return (state with { NotBeforeUtc = now + ProbeLease }, TimeSpan.Zero);
    }, failOpen: TimeSpan.Zero);

    /// <summary>
    /// The relay did not answer, or answered 429/5xx. Schedules the next probe for every process.
    /// <paramref name="retryAfter"/> is the server's Retry-After, when it sent one.
    /// </summary>
    public void ReportHostFailure(TimeSpan? retryAfter = null) => Update(state =>
    {
        var now = _clock();
        var started = state.OutageStartedUtc ?? now;
        var age = now - started;
        var delay = age < FirstDelay ? FirstDelay : age > MaxDelay ? MaxDelay : age;
        // ±20% so many desktops returning from the same server recycle do not arrive in step.
        delay = TimeSpan.FromSeconds(delay.TotalSeconds * (0.8 + _random() * 0.4));
        if (retryAfter is { } requested && requested > delay)
            delay = requested > MaxRetryAfter ? MaxRetryAfter : requested;
        return (new State(started, now + delay), true);
    }, failOpen: false);

    /// <summary>The relay answered (connected, or a per-session status such as 404): the outage is over.</summary>
    public void ReportHostAvailable() => Update(state =>
        state.OutageStartedUtc is null ? (state, false) : (State.Healthy, true), failOpen: false);

    /// <summary>True while an outage is recorded; for tests and diagnostics.</summary>
    internal bool IsInOutage => Update(state => (state, state.OutageStartedUtc is not null), failOpen: false);

    private T Update<T>(Func<State, (State Next, T Result)> change, T failOpen)
    {
        lock (_gate)
        {
            if (_path is null || _lockPath is null)
            {
                var (next, result) = change(_memory);
                _memory = next;
                return result;
            }

            try
            {
                using var held = CrossProcessFileLock.Acquire(_lockPath, LockWait);
                var current = Read(_path);
                var (next, result) = change(current);
                if (next != current)
                    AtomicFile.WriteAllText(_path, next.Serialize());
                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
            {
                Log.Debug(ex, "[Remote] Relay backoff state unavailable; not pacing this attempt");
                return failOpen;
            }
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
