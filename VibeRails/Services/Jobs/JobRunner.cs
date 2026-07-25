using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

/// <summary>
/// Runs inside the terminal window a Job spawned (<c>vb --env … --job-run &lt;id&gt;</c>) and makes that
/// process responsible for its own run.
///
/// The CLI itself is launched by the ordinary interactive path — the user's Environment exactly as
/// they configured it, rendered in this real console via <c>ConsoleOutputConsumer</c> and driven by
/// <c>Console.ReadKey</c>. Nothing about the invocation is automation-specific. What this adds is
/// bookkeeping around it: claim, session link, deadline, cancellation, outcome.
///
/// Owning its own lifetime is what makes the run observable. <c>OwnerProcessId</c> is genuinely this
/// process, so the dashboard's reaper can tell a live run from a dead one; previously it was the
/// shared dashboard PID, which is alive by definition and made the check meaningless.
/// </summary>
public static class JobRunner
{
    /// <summary>Poll interval for the cooperative cancel flag.</summary>
    private static readonly TimeSpan CancelPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Exit code for "this process had no run to execute" — the run id was missing from the DB, or
    /// another process claimed it first. Deliberately distinct from every status code: nothing went
    /// wrong with a run here, because no run was ever started by this process. Mapping it onto
    /// <see cref="JobRunStatus.Queued"/> would tell a supervisor "still queued" for the
    /// already-claimed case, which is the opposite of what happened.
    /// </summary>
    public const int CouldNotStartExitCode = 5;

    /// <summary>
    /// Exit codes are the contract for anything supervising this process, so they must be truthful
    /// rather than uniformly 0 — a caller should not have to read SQLite to learn what happened.
    /// Queued is unmapped on purpose: a process that reaches an exit code has, by definition, gone
    /// past queued, so seeing it here means a caller passed a status that cannot occur.
    /// </summary>
    public static int ToExitCode(JobRunStatus status) => status switch
    {
        JobRunStatus.Succeeded => 0,
        JobRunStatus.Failed => 1,
        JobRunStatus.TimedOut => 2,
        JobRunStatus.Cancelled => 3,
        JobRunStatus.Interrupted => 4,
        _ => CouldNotStartExitCode
    };

    public static async Task<int> RunAsync(ParsedArgs parsedArgs, IServiceProvider services)
    {
        var runId = parsedArgs.JobRunId;
        if (string.IsNullOrWhiteSpace(runId))
            return CouldNotStartExitCode;

        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();

        var run = await store.GetRunAsync(runId);
        if (run is null)
        {
            Log.Warning("[Jobs] --job-run {RunId} not found; nothing to run", runId);
            return CouldNotStartExitCode;
        }

        // Atomic claim. A second launcher of the same run (or a stale retry of the OS tick) loses
        // here and exits quietly rather than opening a duplicate session.
        if (!await store.StartRunAsync(runId, Environment.ProcessId))
        {
            Log.Information("[Jobs] Run {RunId} was already claimed by another process; exiting", runId);
            return CouldNotStartExitCode;
        }

        using var shutdown = new CancellationTokenSource();
        ArmSelfDeadline(store, runId, parsedArgs.MaxRuntimeMinutes);
        var cancelWatcher = WatchForCancellationAsync(store, runId, shutdown.Token);

        var status = JobRunStatus.Succeeded;
        string? error = null;
        var exitCode = 0;

        try
        {
            exitCode = await CliLoop.RunTerminalWithWebAsync(
                parsedArgs,
                services,
                jobRunId: runId,
                onSessionCreated: sessionId =>
                {
                    // Fire-and-forget: linking the recording must never delay or fail the run.
                    _ = Task.Run(async () =>
                    {
                        try { await store.SetRunSessionAsync(runId, sessionId, CancellationToken.None); }
                        catch (Exception ex) { Log.Warning(ex, "[Jobs] Could not link session {SessionId} to run {RunId}", sessionId, runId); }
                    });
                });

            if (exitCode != 0)
            {
                status = JobRunStatus.Failed;
                error = $"{run.Llm} exited with code {exitCode}.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Jobs] Run {RunId} failed", runId);
            status = JobRunStatus.Failed;
            error = ex.Message;
            exitCode = 1;
        }
        finally
        {
            shutdown.Cancel();
            try { await cancelWatcher; } catch { /* watcher is best-effort */ }
        }

        // A cancel that arrived while the CLI was shutting down should be reported as a cancel, not
        // as whatever exit code the CLI happened to produce on its way out.
        if (await WasCancelledAsync(store, runId))
        {
            status = JobRunStatus.Cancelled;
            error = "Job was cancelled.";
        }

        await store.CompleteRunAsync(runId, status, exitCode, error, CancellationToken.None);
        return ToExitCode(status);
    }

    /// <summary>
    /// How long the terminal-status write gets before the kill proceeds regardless. Bounded on
    /// purpose: recording the outcome is worth waiting a moment for, but a wedged SQLite file must
    /// never be able to keep a timed-out Job alive.
    /// </summary>
    private static readonly TimeSpan FinalWriteGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The opt-in absolute deadline: a bare timer on a background thread that records the outcome
    /// and then kills this process tree. The kill must hold no matter where execution is blocked —
    /// inside the CLI, inside a wedged SQLite call, or in a read loop that stopped pumping — so the
    /// status write in front of it is bounded by <see cref="FinalWriteGrace"/> and its failure is
    /// never allowed to skip the kill.
    ///
    /// Writing before killing is what makes the status truthful. Kill-first leaves the row Running
    /// and hands it to the reaper, which can only conclude "the terminal is gone" and records
    /// Interrupted — so TimedOut would never actually appear against a run that timed out.
    ///
    /// Tree kill rather than a plain exit because the CLI spawns its own children (shells, MCP
    /// servers, git); killing only this process would leave those orphaned with nobody watching.
    ///
    /// No deadline is armed when the user did not opt into a timeout — the run then lives until the
    /// CLI exits or the window is closed, which is the intended default.
    /// </summary>
    private static void ArmSelfDeadline(IJobStore store, string runId, int? maxRuntimeMinutes)
    {
        if (maxRuntimeMinutes is not > 0)
            return;

        var deadline = TimeSpan.FromMinutes(maxRuntimeMinutes.Value);
        var thread = new Thread(() =>
        {
            Thread.Sleep(deadline);
            try
            {
                Log.Warning("[Jobs] Run {RunId} hit its {Minutes}-minute limit; killing the process tree", runId, maxRuntimeMinutes.Value);
            }
            catch { /* logging must not stop the kill */ }

            RecordTerminalStatus(
                store,
                runId,
                JobRunStatus.TimedOut,
                $"Job exceeded its {maxRuntimeMinutes.Value}-minute limit.");

            try { Log.CloseAndFlush(); } catch { }

            try
            {
                Process.GetCurrentProcess().Kill(entireProcessTree: true);
            }
            catch
            {
                Environment.Exit(ToExitCode(JobRunStatus.TimedOut));
            }
        })
        {
            IsBackground = true,
            Name = "job-run-deadline"
        };
        thread.Start();
    }

    /// <summary>
    /// Cooperative cancellation from the Jobs page. Unlike the deadline this goes through the store,
    /// so it is best-effort by nature — the deadline is the guarantee, this is the courtesy.
    /// </summary>
    private static async Task WatchForCancellationAsync(IJobStore store, string runId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(CancelPollInterval, cancellationToken);
                if (!await store.IsCancelRequestedAsync(runId, cancellationToken))
                    continue;

                Log.Information("[Jobs] Run {RunId} was cancelled; killing the process tree", runId);

                // Same ordering rule as the deadline: record the outcome first, or the reaper will
                // later see a dead process against a Running row and call it Interrupted.
                RecordTerminalStatus(store, runId, JobRunStatus.Cancelled, "Job was cancelled.");

                try { Log.CloseAndFlush(); } catch { }
                try { Process.GetCurrentProcess().Kill(entireProcessTree: true); }
                catch { Environment.Exit(ToExitCode(JobRunStatus.Cancelled)); }
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — the run finished on its own.
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Jobs] Cancellation watcher for run {RunId} stopped", runId);
        }
    }

    /// <summary>
    /// Writes a run's final status from a path that is about to kill this process, so it is
    /// synchronous (there is no "later" to await on) and bounded (a stuck write must not postpone
    /// the kill). Every failure is swallowed: the kill is the guarantee, this is the bookkeeping.
    ///
    /// Safe to lose. <c>CompleteRunAsync</c> only finalizes a Queued/Running row, so if this does
    /// not land the reaper still closes the run out — just as Interrupted rather than the real
    /// reason. And if it does land, the reaper's later write is a no-op for the same reason.
    /// </summary>
    private static void RecordTerminalStatus(IJobStore store, string runId, JobRunStatus status, string message)
    {
        try
        {
            store.CompleteRunAsync(runId, status, ToExitCode(status), message, CancellationToken.None)
                .WaitAsync(FinalWriteGrace)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Jobs] Could not record {Status} for run {RunId} before killing it", status, runId);
        }
    }

    private static async Task<bool> WasCancelledAsync(IJobStore store, string runId)
    {
        try { return await store.IsCancelRequestedAsync(runId, CancellationToken.None); }
        catch { return false; }
    }
}
