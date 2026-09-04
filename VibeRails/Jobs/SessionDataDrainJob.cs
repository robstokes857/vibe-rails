using VibeRails.DB;
using VibeRails.Services;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;

namespace VibeRails.Jobs;

/// <summary>
/// Drains one completed local session per tick after the user explicitly opts in. The export
/// service owns serialization, compression, transport, cross-process exclusion, and the durable
/// <c>ExportedUTC</c> acknowledgement; this job only schedules eligible work.
/// </summary>
public sealed class SessionDataDrainJob : JobBase
{
    internal static readonly TimeSpan DrainInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan SessionSettleDelay = TimeSpan.FromMinutes(1);

    /// <summary>Backoff applied after a session's first failed attempt.</summary>
    internal static readonly TimeSpan MinRetryBackoff = TimeSpan.FromMinutes(2);

    /// <summary>Ceiling on the backoff. A session is deferred, never abandoned.</summary>
    internal static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISessionDataExportService _exportService;
    private readonly Func<Settings> _loadSettings;
    private readonly TimeProvider _timeProvider;

    public SessionDataDrainJob(
        ILogger<SessionDataDrainJob> logger,
        ISystemResourceService resources,
        IServiceScopeFactory scopeFactory,
        ISessionDataExportService exportService)
        : this(
            logger,
            resources,
            scopeFactory,
            exportService,
            Config.LoadFresh,
            TimeProvider.System)
    {
    }

    internal SessionDataDrainJob(
        ILogger<SessionDataDrainJob> logger,
        ISystemResourceService resources,
        IServiceScopeFactory scopeFactory,
        ISessionDataExportService exportService,
        Func<Settings> loadSettings,
        TimeProvider timeProvider)
        : base(logger, resources)
    {
        _scopeFactory = scopeFactory;
        _exportService = exportService;
        _loadSettings = loadSettings;
        _timeProvider = timeProvider;
    }

    protected override TimeSpan Interval => DrainInterval;
    protected override JobPriority Priority => JobPriority.Low;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Deliberately above the consent gate. A user who opts out (or clears their key) with a
        // spool already on disk is exactly the user whose leftover session material must still
        // be reclaimed, and the sweep only ever deletes -- it never reads or sends anything.
        await _exportService.SweepOrphanedSpoolAsync(cancellationToken);

        if (!_loadSettings().DataExportOptIn || !_exportService.IsConfigured)
            return;

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var endedBeforeUtc = nowUtc - SessionSettleDelay;
        var pending = await repository.GetOldestUnexportedSessionAsync(
            endedBeforeUtc,
            nowUtc,
            cancellationToken);

        if (pending is null)
            return;

        var sessionId = pending.SessionId;

        // Deliberately one session per tick. Failed/non-acknowledged exports remain unmarked and
        // are retried by a later tick; there is no whole-database fallback.
        var result = await _exportService.ExportSessionAsync(sessionId, cancellationToken);

        // Busy is not a failure of this session — another export (or another root backend) holds
        // the gate, so the same session is simply picked up again on the next tick.
        if (result.Status is SessionDataExportStatus.Success or SessionDataExportStatus.Busy)
            return;

        // Defer rather than abandon. Every transient network condition surfaces as the same
        // UploadFailed, so a failure *cap* would silently discard recoverable sessions; a growing
        // backoff instead stops one bad session from holding the queue head while still retrying
        // it forever. The attempt count came out with the selection, so this is a single write.
        var attempts = pending.Attempts + 1;
        var backoff = BackoffFor(attempts);

        // Anchored on a reading taken AFTER the attempt, not on the one that opened the tick. A
        // failing upload can itself consume the whole first backoff before it gives up (a hung
        // single POST burns exactly PayloadTimeout, which equals MinRetryBackoff), and a deadline
        // measured from before the attempt would then be written already expired - handing the
        // queue head straight back to the session this is supposed to move out of the way.
        // nowUtc still drives the selection parameters above, so those stay consistent with
        // each other.
        var failedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var deferred = await repository.DeferSessionExportAsync(
            sessionId,
            failedAtUtc + backoff,
            cancellationToken);

        _logger.LogWarning(
            "Session data export failed. SessionId={SessionId} Status={Status} Attempts={Attempts} RetryIn={Backoff} Detail={Detail}",
            sessionId,
            result.Status,
            attempts,
            backoff,
            result.Detail ?? "none");
        Serilog.Log.Warning(
            "[SessionDataExport] Failed. sessionId={SessionId} status={Status} attempts={Attempts} retryIn={Backoff} deferred={Deferred} detail={Detail}",
            sessionId,
            result.Status,
            attempts,
            backoff,
            deferred,
            result.Detail ?? "none");
    }

    /// <summary>
    /// Exponential backoff from <see cref="MinRetryBackoff"/>, clamped to
    /// <see cref="MaxRetryBackoff"/>. <paramref name="attempts"/> is the count including the
    /// failure being recorded, so the first failure yields the minimum.
    /// </summary>
    internal static TimeSpan BackoffFor(int attempts)
    {
        if (attempts <= 1)
            return MinRetryBackoff;

        // Shift is capped well below the point where the multiply could overflow a long.
        var shift = Math.Min(attempts - 1, 20);
        var ticks = MinRetryBackoff.Ticks << shift;
        return ticks >= MaxRetryBackoff.Ticks ? MaxRetryBackoff : TimeSpan.FromTicks(ticks);
    }
}
