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
/// While the dashboard is open this does the same work as the OS tick (<see cref="JobTickProcessHost"/>):
/// enqueue what's due, spawn a terminal per queued run, reap runs whose process died. Having both is
/// intentional and safe — every claim is atomic, so whichever gets there first wins and the other
/// no-ops. The dashboard path exists so "Run now" is instant instead of waiting up to a minute.
///
/// It never executes a run itself. Runs live in their own spawned terminal processes, which is what
/// keeps this loop from ever blocking on one, and what lets the reaper below tell a live run from a
/// dead one — OwnerProcessId is the run's own process, not this shared host.
/// </summary>
public sealed class JobSchedulerHostedService : BackgroundService, IJobScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LaunchGrace = TimeSpan.FromMinutes(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IJobStore _store;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public JobSchedulerHostedService(IServiceScopeFactory scopeFactory, IJobStore store)
    {
        _scopeFactory = scopeFactory;
        _store = store;
    }

    public void Kick()
    {
        // Release at most one permit; a coalesced wake is fine since the loop drains everything due.
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only the long-lived dashboard process drives the queue. Foreground CLI launches
        // (vb --env) and the spawned `vb --job-run` executors share this full DI graph but must
        // NOT start scheduling — a job-run process runs only its own run and exits. Both set
        // IsLMBootstrap, so this one check excludes them.
        //
        // Removing this would be catastrophic rather than merely wrong: every job terminal would
        // start enqueueing and launching more job terminals.
        if (ParserConfigs.GetArguments().IsLMBootstrap)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Jobs] Scheduler tick failed");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(PollInterval);
            try
            {
                await _wake.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Either the poll interval elapsed or we're shutting down — loop and re-check.
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        await JobRunReaper.ReapAsync(_store, cancellationToken);
        await _store.FailStalledLaunchesAsync(LaunchGrace, cancellationToken);
        await _store.EnqueueDueSchedulesAsync(DateTime.UtcNow, cancellationToken);

        // Launching is fire-and-forget by nature: the spawned terminal owns the run from here, so
        // this returns promptly no matter how long the run itself takes. Nothing about a long run
        // can stall enqueueing, reaping, or a "Run now" kick.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var launcher = scope.ServiceProvider.GetRequiredService<IJobLaunchService>();
        await launcher.LaunchQueuedRunsAsync(cancellationToken);
    }

}
