using System.Reflection;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Services.Jobs;

public sealed class JobWorker(IJobStore store, JobRunExecutor executor, JobWorkspaceService workspaceService)
{
    private const int MaximumConcurrentRuns = 2;
    private static readonly TimeSpan LeaseStaleAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SchedulePollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ArtifactSweepInterval = TimeSpan.FromMinutes(1);

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var instanceId = Guid.NewGuid().ToString("N");
        var startedUtc = DateTime.UtcNow;
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        var lease = new JobWorkerLeaseRecord(instanceId, Environment.ProcessId, version, startedUtc, startedUtc);
        if (!await store.TryAcquireWorkerLeaseAsync(lease, startedUtc - LeaseStaleAge, cancellationToken))
        {
            Log.Information("[Jobs] Another worker owns the active lease; exiting processId={ProcessId}", Environment.ProcessId);
            return 0;
        }

        await store.MarkRunningRunsInterruptedAsync("The previous Jobs worker stopped before reporting completion.", cancellationToken);
        // Only the lease-holding worker reaches this point (a losing worker exited at the guard
        // above), so sweeping leftover isolated workspaces here cannot delete a live worker's
        // in-flight clone. After the interrupt above no run is Running, and queued runs have not
        // cloned yet, so every remaining workspace belongs to a dead run and is safe to delete.
        workspaceService.SweepOrphanWorkspaces();
        await SweepSnapshotArtifactsAsync(cancellationToken);
        var active = new Dictionary<string, Task>(StringComparer.Ordinal);
        // Executors run on a token linked to the worker token so they can also be cancelled on lease
        // loss (below), not only on process shutdown.
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var nextHeartbeat = DateTime.MinValue;
        var nextSchedulePoll = DateTime.MinValue;
        var nextArtifactSweep = DateTime.UtcNow + ArtifactSweepInterval;
        Log.Information("[Jobs] Worker started instanceId={InstanceId} processId={ProcessId}", instanceId, Environment.ProcessId);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    if (now >= nextHeartbeat)
                    {
                        lease = lease with { HeartbeatUtc = now };
                        await store.UpsertWorkerLeaseAsync(lease, cancellationToken);
                        var currentLease = await store.GetWorkerLeaseAsync(cancellationToken);
                        if (currentLease?.InstanceId != instanceId)
                        {
                            Log.Warning("[Jobs] Worker lease was replaced; exiting instanceId={InstanceId}", instanceId);
                            break;
                        }
                        nextHeartbeat = now + HeartbeatInterval;
                    }

                    if (now >= nextSchedulePoll)
                    {
                        await store.EnqueueDueSchedulesAsync(now, cancellationToken);
                        nextSchedulePoll = now + SchedulePollInterval;
                    }

                    if (now >= nextArtifactSweep)
                    {
                        await SweepSnapshotArtifactsAsync(cancellationToken);
                        nextArtifactSweep = now + ArtifactSweepInterval;
                    }

                    foreach (var completed in active.Where(pair => pair.Value.IsCompleted).ToList())
                    {
                        try { await completed.Value; }
                        catch (Exception ex) { Log.Error(ex, "[Jobs] Unhandled run task failure for {RunId}", completed.Key); }
                        active.Remove(completed.Key);
                    }

                    while (active.Count < MaximumConcurrentRuns)
                    {
                        var run = await store.ClaimNextRunAsync(instanceId, Environment.ProcessId, cancellationToken);
                        if (run is null) break;
                        active[run.Id] = executor.ExecuteAsync(run, runCts.Token);
                    }

                    await Task.Delay(active.Count == 0 ? 1000 : 250, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never let a transient failure (a SQLite lock outliving busy_timeout, a poison
                    // schedule, a claim hiccup) tear down the worker — log and retry next iteration.
                    Log.Error(ex, "[Jobs] Worker loop iteration failed; continuing");
                    try { await Task.Delay(1000, cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        finally
        {
            // Cancel in-flight executors before awaiting them. On lease loss the replacement worker
            // has already marked these runs Interrupted; cancelling makes each executor kill its
            // child process tree and stop writing the working tree instead of running to completion
            // against a repository this worker no longer owns.
            runCts.Cancel();
            try { await Task.WhenAll(active.Values); }
            catch (Exception ex) { Log.Warning(ex, "[Jobs] One or more runs failed during worker shutdown"); }
            await store.DeleteWorkerLeaseAsync(instanceId, CancellationToken.None);
            Log.Information("[Jobs] Worker stopped instanceId={InstanceId}", instanceId);
        }
        return 0;
    }

    private async Task SweepSnapshotArtifactsAsync(CancellationToken cancellationToken)
    {
        using var sweepLease = workspaceService.TryAcquireArtifactSweepLease();
        if (sweepLease is null)
            return;
        var references = await store.GetActiveSnapshotArtifactReferencesAsync(cancellationToken);
        workspaceService.SweepSnapshotArtifacts(sweepLease, references);
    }
}
