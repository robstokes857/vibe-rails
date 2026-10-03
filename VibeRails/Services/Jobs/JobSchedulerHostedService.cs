using VibeRails.Services.Terminal;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using VibeRails.DB;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Jira;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

public interface IJobScheduler
{
    /// <summary>Wake the scheduler immediately (e.g. after enqueuing a manual "Run now").</summary>
    void Kick();
}

/// <summary>
/// While VibeRails is active this enqueues what's due, launches queued runs, and reaps runs whose
/// process died. Multiple active VibeRails instances share one SQLite lease, so only its current
/// owner drives the timer, enqueues, reaps and opens native-terminal runs. Every open root still
/// opens the queued Board runs of its own project each cycle, because a terminal tab lives in the
/// process that spawns it and must appear in the window the card lives in.
///
/// It never executes a run itself. Runs live in their own spawned terminal processes, which is what
/// keeps this loop from ever blocking on one, and what lets the reaper below tell a live run from a
/// dead one — OwnerProcessId is the run's own process, not this shared host.
/// </summary>
public sealed class JobSchedulerHostedService : BackgroundService, IJobScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LaunchGrace = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan SchedulerLeaseDuration = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a root's presence row outlives its last scheduler cycle. A root that has not
    /// polled for this long counts as a closed window, the same threshold the scheduler lease
    /// uses; until then the lease holder leaves that project's Board runs to it.
    /// </summary>
    internal static readonly TimeSpan ProjectRootPresenceDuration = SchedulerLeaseDuration;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IJobStore _store;
    private readonly JobSchedulerHealth _health;
    private readonly IJiraPullScheduler? _jira;
    private readonly IBoardSyncScheduler? _boardSync;
    private readonly IBoardProjectResolver? _projectResolver;
    private readonly string _ownerId = $"{Environment.ProcessId}:{Guid.NewGuid():N}";
    private bool _presenceRecorded;
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private bool _ownsLease;

    // Keep a long launch batch from outliving the lease acquired at the top of its cycle. Internal
    // setter is a test seam; production renews three times within each lease window.
    internal TimeSpan LeaseRenewalInterval { get; set; } = SchedulerLeaseDuration / 3;

    // Seam for tests: the production check reads global argv state, which a test would
    // otherwise have to mutate (and restore) process-wide to get the loop running.
    internal Func<bool> IsBootstrapProcess { get; set; } =
        static () => ParserConfigs.GetArguments().IsLMBootstrap;

    public JobSchedulerHostedService(
        IServiceScopeFactory scopeFactory,
        IJobStore store,
        JobSchedulerHealth? health = null,
        IJiraPullScheduler? jira = null,
        IBoardSyncScheduler? boardSync = null,
        IBoardProjectResolver? projectResolver = null)
    {
        _scopeFactory = scopeFactory;
        _store = store;
        _health = health ?? new JobSchedulerHealth();
        _jira = jira;
        _boardSync = boardSync;
        _projectResolver = projectResolver;
    }

    public void Kick()
    {
        // Capacity one deliberately coalesces bursts of Run Now/retry signals. If TryWrite returns
        // false, a wake is already pending and the next cycle will drain the whole durable queue.
        _wake.Writer.TryWrite(0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // DI hosts this service only in active root backends. Keep the bootstrap guard as a second
        // line of defense because foreground `vb --env` and `--job-run` processes share the graph
        // and must run only their own CLI session.
        if (IsBootstrapProcess())
            return;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunCycleAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _health.CycleFailed(DateTime.UtcNow, ex);
                    Log.Error(ex, "[Jobs] Scheduler cycle failed");
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(PollInterval);
                try
                {
                    await _wake.Reader.ReadAsync(timeout.Token);
                    while (_wake.Reader.TryRead(out _))
                    {
                        // Drain any coalesced signal before the next cycle.
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // The poll interval elapsed — loop and renew or contend for the lease.
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _wake.Writer.TryComplete();
            // A background Jira pull observes the stopping token; let it unwind before the host
            // disposes the services it is using. It never throws.
            if (_jira is not null)
                await _jira.WhenIdleAsync();
            if (_boardSync is not null)
                await _boardSync.WhenIdleAsync();
            await ReleasePresenceAsync();
            if (_ownsLease)
            {
                try
                {
                    if (await _store.ReleaseSchedulerLeaseAsync(_ownerId, CancellationToken.None))
                    {
                        Log.Information("[Jobs] Scheduler lease released by {OwnerId}", _ownerId);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Jobs] Could not release scheduler lease for {OwnerId}", _ownerId);
                }
                _ownsLease = false;
                _health.LeaseChanged(false);
            }
        }
    }

    /// <summary>
    /// Every root owns its own terminal hosts, independently of scheduler lease ownership. A failed close
    /// is logged and retried next cycle; it must not skip reaping, enqueueing or launching.
    /// </summary>
    private async Task CloseThisRootsFinishedAutomationTabsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var terminalScope = _scopeFactory.CreateAsyncScope();
            var tabs = terminalScope.ServiceProvider.GetService<ITerminalTabHostService>();
            if (tabs is not null) await tabs.CloseCompletedAutomationTabsAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "[Jobs] Closing finished Automation tabs failed; it will retry on the next cycle");
        }
    }

    internal async Task<bool> RunCycleAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        _health.CycleStarted(nowUtc);
        var previouslyOwnedLease = _ownsLease;
        _ownsLease = await _store.TryAcquireOrRenewSchedulerLeaseAsync(
            _ownerId,
            nowUtc,
            SchedulerLeaseDuration,
            cancellationToken);
        _health.LeaseChanged(_ownsLease);
        await RecordPresenceAsync(nowUtc, cancellationToken);
        await RecoverThisProjectsStartersAsync(cancellationToken);
        await CloseThisRootsFinishedAutomationTabsAsync(cancellationToken);
        if (!_ownsLease)
        {
            if (previouslyOwnedLease)
                Log.Information("[Jobs] Scheduler lease lost by {OwnerId}", _ownerId);
            // This root's own Board runs still open here: their terminal tab must live in the
            // window the card is in, not in whichever window happens to hold the lease. The
            // LaunchedUTC row claim keeps this root and the lease holder from opening one twice.
            var launchedHere = await LaunchThisProjectsBoardRunsAsync(cancellationToken);
            _health.CycleContended(DateTime.UtcNow);
            Log.Write(
                launchedHere > 0 ? LogEventLevel.Information : LogEventLevel.Debug,
                "[Jobs] Scheduler cycle healthy; lease is held by another process. launchedForThisProject={Launched}",
                launchedHere);
            return false;
        }

        if (!previouslyOwnedLease)
            Log.Information("[Jobs] Scheduler lease acquired by {OwnerId}", _ownerId);

        var reaped = await JobRunReaper.ReapAsync(_store, cancellationToken);
        var stalled = await _store.FailStalledLaunchesAsync(LaunchGrace, cancellationToken);
        var enqueued = await _store.EnqueueDueSchedulesAsync(nowUtc, cancellationToken);

        // Launching is fire-and-forget by nature: the spawned terminal owns the run from here, so
        // this returns promptly no matter how long the run itself takes. Nothing about a long run
        // can stall enqueueing, reaping, or a "Run now" kick.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var launcher = scope.ServiceProvider.GetRequiredService<IJobLaunchService>();
        var (leaseMaintained, launched) = await LaunchQueuedRunsWithLeaseRenewalAsync(launcher, cancellationToken);
        if (leaseMaintained)
        {
            // About every 15 minutes, started only by the process that holds the scheduler lease
            // and never awaited here: a pull can outlast the lease by minutes and must not delay
            // enqueueing or reaping. Its own OS lock stops a second process (one that picked up
            // the lease meanwhile) from pulling at the same time. Failures stay inside the pull.
            _jira?.Tick(nowUtc, cancellationToken);
            // Every 60 s, same rules: lease holder only, never awaited here, its own OS lock,
            // failures recorded on the board's link rather than thrown (VB-51).
            _boardSync?.Tick(nowUtc, cancellationToken);
        }
        _health.CycleCompleted(
            DateTime.UtcNow,
            leaseMaintained,
            enqueued.Count,
            launched,
            reaped,
            stalled);
        Log.Write(
            GetCycleCompletionLogLevel(enqueued.Count, launched, reaped, stalled),
            "[Jobs] Scheduler cycle complete. lease={OwnsLease} enqueued={Enqueued} launched={Launched} reaped={Reaped} stalled={Stalled}",
            leaseMaintained,
            enqueued.Count,
            launched,
            reaped,
            stalled);
        return leaseMaintained;
    }

    internal static LogEventLevel GetCycleCompletionLogLevel(
        int schedulesEnqueued,
        int runsLaunched,
        int runsReaped,
        int stalledLaunchesFailed) =>
        schedulesEnqueued > 0 || runsLaunched > 0 || runsReaped > 0 || stalledLaunchesFailed > 0
            ? LogEventLevel.Information
            : LogEventLevel.Debug;

    /// <summary>
    /// Says "a window for this project is open" for the next <see cref="ProjectRootPresenceDuration"/>.
    /// The lease holder reads it before opening another project's Board run in its own window. A
    /// failure here is logged, not thrown: the cycle's real work must still happen, and the worst
    /// case is the pre-presence behaviour (fallback after the grace alone).
    /// </summary>
    private async Task RecordPresenceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (_projectResolver is null)
            return;
        try
        {
            var projectPath = await _projectResolver.ResolveAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(projectPath))
                return;
            await _store.RecordProjectRootAsync(_ownerId, projectPath, nowUtc, ProjectRootPresenceDuration, cancellationToken);
            _presenceRecorded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Jobs] Could not record this window's presence for {OwnerId}; another window may open this project's Board runs after the grace period", _ownerId);
        }
    }

    private async Task RecoverThisProjectsStartersAsync(CancellationToken cancellationToken)
    {
        if (_projectResolver is null) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var starters = scope.ServiceProvider.GetService<BoardStarterWorkflowService>();
            if (starters is null) return;
            var project = await _projectResolver.ResolveAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(project)) await starters.RecoverAsync(project, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Jobs] Starter review setup will retry on the next cycle");
        }
    }

    private async Task ReleasePresenceAsync()
    {
        if (!_presenceRecorded)
            return;
        try
        {
            await _store.ReleaseProjectRootAsync(_ownerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Jobs] Could not release this window's presence for {OwnerId}; it expires on its own", _ownerId);
        }
        _presenceRecorded = false;
    }

    private async Task<int> LaunchThisProjectsBoardRunsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var launcher = scope.ServiceProvider.GetRequiredService<IJobLaunchService>();
        return await launcher.LaunchQueuedProjectRunsAsync(cancellationToken);
    }

    private async Task<(bool LeaseMaintained, int Launched)> LaunchQueuedRunsWithLeaseRenewalAsync(
        IJobLaunchService launcher,
        CancellationToken cancellationToken)
    {
        using var launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseLost = 0;
        var renewal = RenewLeaseUntilCancelledAsync(
            launchCancellation,
            () => Interlocked.Exchange(ref leaseLost, 1));

        var launched = 0;
        var launchCompleted = false;
        try
        {
            launched = await launcher.LaunchQueuedRunsAsync(launchCancellation.Token);
            launchCompleted = true;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            Volatile.Read(ref leaseLost) != 0)
        {
            // The renewal loop deliberately cancelled this host's remaining launch work after it
            // lost ownership. Row-level launch claims still protect work already handed off.
        }
        finally
        {
            launchCancellation.Cancel();
            await renewal;
        }

        // The verdict must be read only after the renewal task has fully stopped: a final renewal
        // failure that races the launch batch completing would otherwise be latched after this
        // method captured leaseLost == 0, and CycleCompleted(leaseMaintained: true) would then
        // overwrite OwnsSchedulerLease/LastError with ownership another process actually holds.
        return launchCompleted && Volatile.Read(ref leaseLost) == 0
            ? (true, launched)
            : (false, launchCompleted ? launched : 0);
    }

    private async Task RenewLeaseUntilCancelledAsync(
        CancellationTokenSource launchCancellation,
        Action markLeaseLost)
    {
        using var timer = new PeriodicTimer(LeaseRenewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(launchCancellation.Token))
            {
                bool renewed;
                try
                {
                    renewed = await _store.TryAcquireOrRenewSchedulerLeaseAsync(
                        _ownerId,
                        DateTime.UtcNow,
                        SchedulerLeaseDuration,
                        launchCancellation.Token);
                }
                catch (OperationCanceledException) when (launchCancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "[Jobs] Scheduler lease renewal failed for {OwnerId}", _ownerId);
                    renewed = false;
                }

                if (renewed)
                    continue;

                _ownsLease = false;
                _health.LeaseChanged(false);
                markLeaseLost();
                launchCancellation.Cancel();
                Log.Warning(
                    "[Jobs] Scheduler lease was lost during queued-run launch; remaining launches were stopped for {OwnerId}",
                    _ownerId);
                return;
            }
        }
        catch (OperationCanceledException) when (launchCancellation.IsCancellationRequested)
        {
            // Normal completion: the launch batch finished or the host is stopping.
        }
    }
}
