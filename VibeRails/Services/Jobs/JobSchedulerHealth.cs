namespace VibeRails.Services.Jobs;

/// <summary>
/// Thread-safe, process-local health for the durable Automation scheduler. The dashboard and the
/// VBD control pipe both read this snapshot; durable run state remains in SQLite.
/// </summary>
public sealed class JobSchedulerHealth
{
    internal const int MaximumErrorLength = 1024;
    internal const string LeaseLostError = "The scheduler lease was lost during queued-run launch.";

    private readonly object _gate = new();
    private JobSchedulerHealthSnapshot _snapshot = new();

    public JobSchedulerHealthSnapshot GetSnapshot()
    {
        lock (_gate)
            return _snapshot;
    }

    internal void CycleStarted(DateTime utcNow)
    {
        lock (_gate)
            _snapshot = _snapshot with { LastCycleStartedUtc = AsUtc(utcNow) };
    }

    /// <summary>
    /// Records a healthy scheduler pass that found another process holding the durable lease.
    /// This process did not drain the queue, so the last successful owner cycle and its counters
    /// remain intact.
    /// </summary>
    internal void CycleContended(DateTime utcNow)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastCycleCompletedUtc = AsUtc(utcNow),
                OwnsSchedulerLease = false,
                LastErrorUtc = null,
                LastError = null
            };
        }
    }

    internal void CycleCompleted(
        DateTime utcNow,
        bool ownsLease,
        int schedulesEnqueued,
        int runsLaunched,
        int runsReaped,
        int stalledLaunchesFailed)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastCycleCompletedUtc = AsUtc(utcNow),
                // Losing the durable lease part-way through a launch batch is not a successful
                // owner cycle. Preserve the previous success timestamp for diagnostics.
                LastSuccessfulCycleUtc = ownsLease
                    ? AsUtc(utcNow)
                    : _snapshot.LastSuccessfulCycleUtc,
                OwnsSchedulerLease = ownsLease,
                LastSchedulesEnqueued = schedulesEnqueued,
                LastRunsLaunched = runsLaunched,
                LastRunsReaped = runsReaped,
                LastStalledLaunchesFailed = stalledLaunchesFailed,
                LastErrorUtc = ownsLease ? null : AsUtc(utcNow),
                LastError = ownsLease ? null : LeaseLostError
            };
        }
    }

    internal void CycleFailed(DateTime utcNow, Exception exception)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastCycleCompletedUtc = AsUtc(utcNow),
                LastErrorUtc = AsUtc(utcNow),
                LastError = BoundError(exception)
            };
        }
    }

    internal void LeaseChanged(bool ownsLease)
    {
        lock (_gate)
            _snapshot = _snapshot with { OwnsSchedulerLease = ownsLease };
    }

    private static string BoundError(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        if (message.Length <= MaximumErrorLength)
            return message;

        return message[..(MaximumErrorLength - 1)] + "…";
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

public sealed record JobSchedulerHealthSnapshot(
    DateTime? LastCycleStartedUtc = null,
    DateTime? LastCycleCompletedUtc = null,
    DateTime? LastSuccessfulCycleUtc = null,
    bool OwnsSchedulerLease = false,
    int LastSchedulesEnqueued = 0,
    int LastRunsLaunched = 0,
    int LastRunsReaped = 0,
    int LastStalledLaunchesFailed = 0,
    DateTime? LastErrorUtc = null,
    string? LastError = null);
