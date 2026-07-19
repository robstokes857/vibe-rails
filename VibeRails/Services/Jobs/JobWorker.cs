using System.Reflection;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Services.Jobs;

public sealed class JobWorker(IJobStore store, JobRunExecutor executor)
{
    private const int MaximumConcurrentRuns = 2;
    private static readonly TimeSpan LeaseStaleAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SchedulePollInterval = TimeSpan.FromSeconds(10);

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
        var active = new Dictionary<string, Task>(StringComparer.Ordinal);
        var nextHeartbeat = DateTime.MinValue;
        var nextSchedulePoll = DateTime.MinValue;
        Log.Information("[Jobs] Worker started instanceId={InstanceId} processId={ProcessId}", instanceId, Environment.ProcessId);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
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
                    active[run.Id] = executor.ExecuteAsync(run, cancellationToken);
                }

                await Task.Delay(active.Count == 0 ? 1000 : 250, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal service shutdown.
        }
        finally
        {
            try { await Task.WhenAll(active.Values); }
            catch (Exception ex) { Log.Warning(ex, "[Jobs] One or more runs failed during worker shutdown"); }
            await store.DeleteWorkerLeaseAsync(instanceId, CancellationToken.None);
            Log.Information("[Jobs] Worker stopped instanceId={InstanceId}", instanceId);
        }
        return 0;
    }
}
