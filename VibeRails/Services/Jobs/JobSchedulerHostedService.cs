using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using VibeRails.DB;
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
/// owner drives the timer and drains the durable queue.
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

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IJobStore _store;
    private readonly string _ownerId = $"{Environment.ProcessId}:{Guid.NewGuid():N}";
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

    public JobSchedulerHostedService(IServiceScopeFactory scopeFactory, IJobStore store)
    {
        _scopeFactory = scopeFactory;
        _store = store;
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
            if (_ownsLease)
            {
                try
                {
                    await _store.ReleaseSchedulerLeaseAsync(_ownerId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Jobs] Could not release scheduler lease for {OwnerId}", _ownerId);
                }
                _ownsLease = false;
            }
        }
    }

    internal async Task<bool> RunCycleAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        _ownsLease = await _store.TryAcquireOrRenewSchedulerLeaseAsync(
            _ownerId,
            nowUtc,
            SchedulerLeaseDuration,
            cancellationToken);
        if (!_ownsLease)
            return false;

        await JobRunReaper.ReapAsync(_store, cancellationToken);
        await _store.FailStalledLaunchesAsync(LaunchGrace, cancellationToken);
        await _store.EnqueueDueSchedulesAsync(nowUtc, cancellationToken);

        // Launching is fire-and-forget by nature: the spawned terminal owns the run from here, so
        // this returns promptly no matter how long the run itself takes. Nothing about a long run
        // can stall enqueueing, reaping, or a "Run now" kick.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var launcher = scope.ServiceProvider.GetRequiredService<IJobLaunchService>();
        return await LaunchQueuedRunsWithLeaseRenewalAsync(launcher, cancellationToken);
    }

    private async Task<bool> LaunchQueuedRunsWithLeaseRenewalAsync(
        IJobLaunchService launcher,
        CancellationToken cancellationToken)
    {
        using var launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseLost = 0;
        var renewal = RenewLeaseUntilCancelledAsync(
            launchCancellation,
            () => Interlocked.Exchange(ref leaseLost, 1));

        try
        {
            await launcher.LaunchQueuedRunsAsync(launchCancellation.Token);
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

        return Volatile.Read(ref leaseLost) == 0;
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
