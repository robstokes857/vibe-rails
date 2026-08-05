using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using static VibeRails.DTOs.JobRunOutcome;

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

    private const int ShutdownClaimNone = 0;
    private const int ShutdownClaimCooperative = 1;
    private const int ShutdownClaimDeadline = 2;
    private const int ShutdownClaimIdleFallback = 3;

    // Exit codes and the cancellation message live in DTOs/JobsDtos.cs (JobRunOutcome) so the
    // JobStore's atomic completion SQL can share them without depending on this Services layer.

    public static async Task<int> RunAsync(ParsedArgs parsedArgs, IServiceProvider services)
    {
        var runId = parsedArgs.JobRunId;
        if (string.IsNullOrWhiteSpace(runId))
            return CouldNotStartExitCode;

        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var automationConsumer = scope.ServiceProvider.GetRequiredService<IAutomationConsumer>();

        var run = await store.GetRunAsync(runId);
        if (run is null)
        {
            Log.Warning("[Jobs] --job-run {RunId} not found; nothing to run", runId);
            return CouldNotStartExitCode;
        }

        // Atomic claim. A second launcher of the same run during a stale lease handoff loses here
        // and exits quietly rather than opening a duplicate session.
        if (!await store.StartRunAsync(runId, Environment.ProcessId))
        {
            Log.Information("[Jobs] Run {RunId} was already claimed by another process; exiting", runId);
            return CouldNotStartExitCode;
        }

        using var shutdown = new CancellationTokenSource();
        var shutdownState = new JobRunShutdownState();
        ArmSelfDeadline(store, runId, parsedArgs.MaxRuntimeMinutes, shutdownState);
        var cancelWatcher = WatchForCancellationAsync(store, runId, shutdown.Token);
        using var idleFallbackRegistration = ArmIdleShutdownFallback(
            store,
            runId,
            automationConsumer.IdleShutdownToken,
            shutdownState);

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
                    // Sessions_LinkJobRunSession stored the backlink atomically with the Sessions
                    // row. Keep the callback only as an explicit diagnostic breadcrumb.
                    Log.Information("[Jobs] Run {RunId} is recording terminal session {SessionId}", runId, sessionId);
                },
                cancellationToken: automationConsumer.IdleShutdownToken);

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
            // Claim ordinary/cooperative completion as soon as the terminal invocation returns.
            // The deadline claims the same gate, so exactly one side decides whether this run
            // finished before or at its absolute limit.
            Interlocked.CompareExchange(
                ref shutdownState.OutcomeClaim,
                ShutdownClaimCooperative,
                ShutdownClaimNone);
            shutdown.Cancel();
            try { await cancelWatcher; } catch { /* watcher is best-effort */ }
        }

        // A cancel that arrived while the CLI was shutting down should be reported as a cancel, not
        // as whatever exit code the CLI happened to produce on its way out.
        (status, exitCode, error) = ResolveShutdownOutcome(
            status,
            exitCode,
            error,
            automationConsumer.IdleShutdownRequested,
            await WasCancelledAsync(store, runId));

        // The absolute runtime limit has higher priority than idle completion. Usually its owning
        // thread records and kills immediately; this also keeps the cooperative path truthful if
        // both signals arrive while terminal cleanup is unwinding.
        var deadlineWon = Volatile.Read(ref shutdownState.OutcomeClaim) == ShutdownClaimDeadline;
        if (deadlineWon)
        {
            status = JobRunStatus.TimedOut;
            exitCode = ToExitCode(status);
            error = $"Automation exceeded its {parsedArgs.MaxRuntimeMinutes}-minute limit.";
        }

        if (automationConsumer.IdleShutdownRequested
            && !deadlineWon)
        {
            // Atomic with CancelRequested: an explicit cancel that lands after the best-effort read
            // above still wins over idle success in the durable JobRuns row.
            status = await store.CompleteIdleRunAsync(runId, CancellationToken.None);
            exitCode = ToExitCode(status);
            error = status == JobRunStatus.Cancelled ? CancelledMessage : null;
        }
        else
        {
            await store.CompleteRunAsync(runId, status, exitCode, error, CancellationToken.None);
        }
        Volatile.Write(ref shutdownState.RunFinalized, 1);
        return ToExitCode(status);
    }

    /// <summary>
    /// How long the terminal-status write gets before the kill proceeds regardless. Bounded on
    /// purpose: recording the outcome is worth waiting a moment for, but a wedged SQLite file must
    /// never be able to keep a timed-out Job alive.
    /// </summary>
    private static readonly TimeSpan FinalWriteGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Idle shutdown normally travels cooperatively through TerminalRunner so its PTY and session
    /// records are closed cleanly. If that unwind or the final DB write wedges, this fallback still
    /// records successful idle completion and terminates the whole Job process tree.
    /// </summary>
    private static CancellationTokenRegistration ArmIdleShutdownFallback(
        IJobStore store,
        string runId,
        CancellationToken idleShutdownToken,
        JobRunShutdownState state)
    {
        return idleShutdownToken.Register(() =>
        {
            var thread = new Thread(() =>
            {
                Thread.Sleep(FinalWriteGrace);
                if (Volatile.Read(ref state.RunFinalized) != 0)
                    return;

                var claim = Volatile.Read(ref state.OutcomeClaim);
                if (claim == ShutdownClaimDeadline)
                    return;

                if (claim == ShutdownClaimNone)
                {
                    claim = Interlocked.CompareExchange(
                        ref state.OutcomeClaim,
                        ShutdownClaimIdleFallback,
                        ShutdownClaimNone);
                    if (claim == ShutdownClaimDeadline)
                        return;
                }

                try
                {
                    Log.Warning(
                        "[Jobs] Run {RunId} did not close within the Automation idle grace period; killing the process tree",
                        runId);
                }
                catch { /* logging must not stop the kill */ }

                var fallbackStatus = RecordIdleTerminalStatus(store, runId);

                // The cooperative path may have finalized while the bounded fallback write was in
                // progress. In that case let Program stop the host normally instead of racing it.
                if (Volatile.Read(ref state.RunFinalized) != 0)
                    return;

                try { Log.CloseAndFlush(); } catch { }
                try { Process.GetCurrentProcess().Kill(entireProcessTree: true); }
                catch { Environment.Exit(ToExitCode(fallbackStatus)); }
            })
            {
                IsBackground = true,
                Name = "job-run-idle-shutdown"
            };
            thread.Start();
        });
    }

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
    /// Once the terminal invocation returns it atomically claims cooperative completion; final DB
    /// bookkeeping is then outside the configured Automation runtime.
    /// </summary>
    private static void ArmSelfDeadline(
        IJobStore store,
        string runId,
        int? maxRuntimeMinutes,
        JobRunShutdownState state)
    {
        if (maxRuntimeMinutes is not > 0)
            return;

        var deadline = TimeSpan.FromMinutes(maxRuntimeMinutes.Value);
        var thread = new Thread(() =>
        {
            Thread.Sleep(deadline);
            if (Volatile.Read(ref state.RunFinalized) != 0)
                return;

            // The terminal-return path claims Cooperative in its finally block. If it won, the
            // Automation itself completed before the absolute limit and only bookkeeping remains.
            // If this deadline wins, idle/main completion can no longer persist Succeeded.
            if (Interlocked.CompareExchange(
                    ref state.OutcomeClaim,
                    ShutdownClaimDeadline,
                    ShutdownClaimNone) != ShutdownClaimNone)
            {
                return;
            }

            try
            {
                Log.Warning("[Jobs] Run {RunId} hit its {Minutes}-minute limit; killing the process tree", runId, maxRuntimeMinutes.Value);
            }
            catch { /* logging must not stop the kill */ }

            RecordTerminalStatus(
                store,
                runId,
                JobRunStatus.TimedOut,
                $"Automation exceeded its {maxRuntimeMinutes.Value}-minute limit.");

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
                RecordTerminalStatus(store, runId, JobRunStatus.Cancelled, CancelledMessage);

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
    private static void RecordTerminalStatus(IJobStore store, string runId, JobRunStatus status, string? message)
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

    private static JobRunStatus RecordIdleTerminalStatus(IJobStore store, string runId)
    {
        try
        {
            return store.CompleteIdleRunAsync(runId, CancellationToken.None)
                .WaitAsync(FinalWriteGrace)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Jobs] Could not record idle completion for run {RunId} before killing it", runId);
            return JobRunStatus.Succeeded;
        }
    }

    private static async Task<bool> WasCancelledAsync(IJobStore store, string runId)
    {
        try { return await store.IsCancelRequestedAsync(runId, CancellationToken.None); }
        catch { return false; }
    }

    internal static (JobRunStatus Status, int ExitCode, string? Error) ResolveShutdownOutcome(
        JobRunStatus currentStatus,
        int currentExitCode,
        string? currentError,
        bool idleShutdownRequested,
        bool cancellationRequested)
    {
        // Two minutes without raw PTY bytes is the Automation completion signal. TerminalRunner
        // has already unwound the external-session registration and disposed the PTY by this point.
        if (idleShutdownRequested)
        {
            currentStatus = JobRunStatus.Succeeded;
            currentExitCode = 0;
            currentError = null;
        }

        // Explicit user intent wins if cancellation and idle shutdown race each other.
        if (cancellationRequested)
        {
            currentStatus = JobRunStatus.Cancelled;
            currentExitCode = ToExitCode(currentStatus);
            currentError = CancelledMessage;
        }

        return (currentStatus, currentExitCode, currentError);
    }

    private sealed class JobRunShutdownState
    {
        public int RunFinalized;
        public int OutcomeClaim;
    }
}
