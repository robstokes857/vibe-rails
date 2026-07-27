using System.Diagnostics;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Services.Jobs;

/// <summary>
/// Finalizes runs whose owning process is gone — the user closed the terminal window, the machine
/// was rebooted mid-run, or the process was killed. <c>OwnerProcessId</c> is the run's own spawned
/// process, so "is it still alive" is a meaningful question here.
///
/// Called by the active root scheduler. A Running row that nothing ever closes out is not merely
/// untidy: it fails the per-job overlap guard and counts against the machine-wide launch cap for
/// good. The next active VibeRails root repairs those rows before it considers new work.
/// </summary>
internal static class JobRunReaper
{
    public static async Task<int> ReapAsync(IJobStore store, CancellationToken cancellationToken = default)
    {
        var active = await store.GetActiveRunsAsync(cancellationToken);
        var reaped = 0;

        foreach (var run in active)
        {
            if (run.OwnerProcessId is int pid && IsOwnerAlive(pid, run.StartedUtc))
                continue;

            // Only ever recorded as Interrupted — a run whose process died without saying why is
            // exactly that. Timeout and cancellation record their own status before killing
            // themselves, and CompleteRunAsync will not overwrite a row that is already terminal,
            // so this cannot relabel a run that reported a real outcome.
            await store.CompleteRunAsync(run.Id, JobRunStatus.Interrupted, null,
                "The terminal running this Automation is no longer open.", cancellationToken);
            reaped++;
        }

        return reaped;
    }

    /// <summary>
    /// "Is the process that claimed this run still running" — which is not the same question as
    /// "does this PID exist". PIDs are recycled, and a run whose owner died only to have its PID
    /// reused by something unrelated would look alive forever: never reaped, so its job's overlap
    /// guard never clears and its slot in the launch cap is never returned. Unlikely per run, but
    /// it is a permanent wedge when it happens, and it gets likelier the longer a machine is up.
    ///
    /// Start time settles it without a schema change. The owner process necessarily existed before
    /// it claimed the run and stamped StartedUTC, so a process claiming to be the owner cannot have
    /// started after that moment — anything that did is a different process wearing a recycled PID.
    /// A second of slack absorbs clock granularity between Process.StartTime and the DB timestamp.
    /// </summary>
    private static bool IsOwnerAlive(int pid, DateTime? startedUtc)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
                return false;

            if (startedUtc is not DateTime claimedUtc)
                return true;

            return process.StartTime.ToUniversalTime() <= claimedUtc.AddSeconds(1);
        }
        catch
        {
            // Gone, or not ours to inspect. Either way nothing is going to finish this run.
            return false;
        }
    }
}
