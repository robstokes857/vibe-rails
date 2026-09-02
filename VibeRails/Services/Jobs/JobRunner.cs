using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Cli;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using static VibeRails.DTOs.JobRunOutcome;

namespace VibeRails.Services.Jobs;

/// <summary>
/// Runs inside the native terminal window an Automation spawned
/// (<c>vb [--env …] --job-run &lt;id&gt;</c>) and makes that process responsible for its own run.
///
/// Repository scripts run sequentially through explicit interpreters with discrete argv and stream
/// into this console. An optional Worker enters the ordinary interactive CLI path exactly as the
/// user configured its Environment. This layer adds ordering plus bookkeeping: action status/output,
/// run claim, session link, deadline, cancellation, and final outcome.
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
        var scriptService = scope.ServiceProvider.GetRequiredService<IAutomationScriptService>();
        var cli = scope.ServiceProvider.GetRequiredService<ICliWrapper>();

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

        var status = JobRunStatus.Succeeded;
        string? error = null;
        var exitCode = 0;
        var actions = run.Actions?.OrderBy(action => action.Position).ToList() ?? [];
        var workspaceRoot = string.IsNullOrWhiteSpace(parsedArgs.WorkDir)
            ? run.ProjectPath
            : Path.GetFullPath(parsedArgs.WorkDir);

        try
        {
            // A pre-workflow snapshot can exist only on an older database whose migration could
            // not yet run. Preserve that one-Worker behavior rather than treating it as an empty
            // successful workflow.
            if (actions.Count == 0)
            {
                var legacy = await RunWorkerActionAsync(
                    parsedArgs,
                    services,
                    store,
                    runId,
                    action: null,
                    automationConsumer,
                    shutdownState,
                    isLastAction: true);
                status = legacy.RunStatus;
                exitCode = legacy.ExitCode;
                error = legacy.Error;
            }
            else
            {
                for (var index = 0; index < actions.Count; index++)
                {
                    var action = actions[index];
                    if (!await store.StartRunActionAsync(runId, action.Id, CancellationToken.None))
                    {
                        status = JobRunStatus.Failed;
                        exitCode = 1;
                        error = $"Action {index + 1} could not be started because its snapshot changed.";
                        break;
                    }

                    Console.WriteLine();
                    Console.WriteLine($"[VibeRails] Automation action {index + 1}/{actions.Count}: {DescribeAction(action)}");

                    ActionOutcome outcome;
                    try
                    {
                        outcome = action.Kind switch
                        {
                            JobActionKind.Script => await RunScriptActionAsync(
                                cli,
                                scriptService,
                                workspaceRoot,
                                run.ProjectPath,
                                action),
                            JobActionKind.Worker => await RunWorkerActionAsync(
                                parsedArgs,
                                services,
                                store,
                                runId,
                                action,
                                automationConsumer,
                                shutdownState,
                                isLastAction: index == actions.Count - 1),
                            _ => ActionOutcome.Failed($"Action {index + 1} has an unknown kind.")
                        };
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[Jobs] Action {ActionId} in run {RunId} failed", action.Id, runId);
                        outcome = ActionOutcome.Failed(ex.Message);
                    }

                    await store.CompleteRunActionAsync(
                        runId,
                        action.Id,
                        outcome.ActionStatus,
                        outcome.ExitCode,
                        outcome.Error,
                        TruncateCapturedOutput(outcome.StandardOutput),
                        TruncateCapturedOutput(outcome.StandardError),
                        CancellationToken.None);

                    if (outcome.ActionStatus == JobRunActionStatus.Succeeded)
                        continue;

                    status = outcome.RunStatus;
                    exitCode = outcome.ExitCode;
                    error = outcome.Error;
                    break; // Ordered workflows are deliberately fail-fast.
                }
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

        // A cancel that arrived while an action was shutting down should be reported as a cancel,
        // not as whatever exit code that action happened to produce on its way out.
        if (await WasCancelledAsync(store, runId))
        {
            status = JobRunStatus.Cancelled;
            exitCode = ToExitCode(status);
            error = CancelledMessage;
        }

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

        if (status == JobRunStatus.Succeeded)
        {
            // Success goes through the store's cancel-aware completion so a Stop that lands after
            // the cancel read above is recorded as Cancelled instead of being overwritten by a
            // later Succeeded. If another terminal path (deadline, cancel watcher, reaper) already
            // closed the run, its durable status comes back and decides the exit code.
            status = await store.CompleteIdleRunAsync(runId, CancellationToken.None);
        }
        else
        {
            await store.CompleteRunAsync(runId, status, exitCode, error, CancellationToken.None);
        }
        Volatile.Write(ref shutdownState.RunFinalized, 1);
        return ToExitCode(status);
    }

    private static async Task<ActionOutcome> RunScriptActionAsync(
        ICliWrapper cli,
        IAutomationScriptService scriptService,
        string workspaceRoot,
        string projectRoot,
        JobRunActionRecord action)
    {
        PreparedAutomationScript prepared;
        try
        {
            prepared = await scriptService.PrepareAsync(workspaceRoot, projectRoot, action, CancellationToken.None);
        }
        catch (AutomationScriptValidationException ex)
        {
            return ActionOutcome.Failed(ex.Message);
        }

        var timeout = action.TimeoutSeconds is int seconds
            ? TimeSpan.FromSeconds(seconds)
            : Timeout.InfiniteTimeSpan;
        var result = await cli.RunAsync(
            new CliRequest(
                prepared.Executable,
                prepared.Arguments,
                prepared.WorkingDirectory,
                prepared.EnvironmentVariables,
                timeout),
            line =>
            {
                if (line.IsError)
                    Console.Error.WriteLine(line.Text);
                else
                    Console.WriteLine(line.Text);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        if (result.TimedOut)
        {
            return new ActionOutcome(
                JobRunActionStatus.TimedOut,
                JobRunStatus.TimedOut,
                ToExitCode(JobRunStatus.TimedOut),
                $"Script '{action.ScriptPath}' exceeded its {action.TimeoutSeconds}-second limit.",
                result.StandardOutput,
                result.StandardError);
        }

        if (result.Cancelled)
        {
            return new ActionOutcome(
                JobRunActionStatus.Cancelled,
                JobRunStatus.Cancelled,
                ToExitCode(JobRunStatus.Cancelled),
                CancelledMessage,
                result.StandardOutput,
                result.StandardError);
        }

        if (result.ExitCode != 0)
        {
            return new ActionOutcome(
                JobRunActionStatus.Failed,
                JobRunStatus.Failed,
                result.ExitCode,
                $"Script '{action.ScriptPath}' exited with code {result.ExitCode}.",
                result.StandardOutput,
                result.StandardError);
        }

        return new ActionOutcome(
            JobRunActionStatus.Succeeded,
            JobRunStatus.Succeeded,
            0,
            null,
            result.StandardOutput,
            result.StandardError);
    }

    private static async Task<ActionOutcome> RunWorkerActionAsync(
        ParsedArgs parsedArgs,
        IServiceProvider services,
        IJobStore store,
        string runId,
        JobRunActionRecord? action,
        IAutomationConsumer automationConsumer,
        JobRunShutdownState shutdownState,
        bool isLastAction)
    {
        if (!parsedArgs.IsLMBootstrap)
            return ActionOutcome.Failed("The Worker action was launched without its Environment.");

        Volatile.Write(ref shutdownState.WorkerPhaseComplete, 0);
        using var idleFallbackRegistration = ArmWorkerIdleShutdownFallback(
            store,
            runId,
            automationConsumer.IdleShutdownToken,
            shutdownState,
            isLastAction);

        var workerExitCode = 0;
        try
        {
            workerExitCode = await CliLoop.RunTerminalWithWebAsync(
                parsedArgs,
                services,
                jobRunId: runId,
                onSessionCreated: sessionId =>
                {
                    Log.Information(
                        "[Jobs] Run {RunId} is recording Worker terminal session {SessionId}",
                        runId,
                        sessionId);
                    if (action is null)
                        return;

                    try
                    {
                        store.LinkRunActionSessionAsync(
                                runId,
                                action.Id,
                                sessionId,
                                CancellationToken.None)
                            .WaitAsync(FinalWriteGrace)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(
                            ex,
                            "[Jobs] Could not link Worker action {ActionId} to session {SessionId}",
                            action.Id,
                            sessionId);
                    }
                },
                cancellationToken: automationConsumer.IdleShutdownToken);
        }
        catch (OperationCanceledException) when (automationConsumer.IdleShutdownRequested)
        {
            // Raw-output idleness is the established signal that an Automation Worker has
            // finished. It completes this action only; post-Worker scripts still get to run.
            workerExitCode = 0;
        }
        finally
        {
            // The fallback callback may already be sleeping. Publish this before any post-Worker
            // action begins so that callback cannot kill a healthy workflow five seconds later.
            Volatile.Write(ref shutdownState.WorkerPhaseComplete, 1);
        }

        if (automationConsumer.IdleShutdownRequested)
            return ActionOutcome.Succeeded;

        return workerExitCode == 0
            ? ActionOutcome.Succeeded
            : ActionOutcome.Failed($"Worker exited with code {workerExitCode}.", workerExitCode);
    }

    private static string DescribeAction(JobRunActionRecord action) => action.Kind switch
    {
        JobActionKind.Worker => $"Worker — {action.EnvironmentName ?? action.Llm.ToString()}",
        JobActionKind.Script => $"{action.ScriptRuntime} — {action.ScriptPath}",
        _ => action.Kind.ToString()
    };

    private const int MaximumCapturedOutputCharacters = 1_000_000;

    private static string TruncateCapturedOutput(string value)
    {
        if (value.Length <= MaximumCapturedOutputCharacters)
            return value;

        const string marker = "\n[Output truncated by VibeRails]\n";
        return value[..(MaximumCapturedOutputCharacters - marker.Length)] + marker;
    }

    /// <summary>
    /// How long the terminal-status write gets before the kill proceeds regardless. Bounded on
    /// purpose: recording the outcome is worth waiting a moment for, but a wedged SQLite file must
    /// never be able to keep a timed-out Job alive.
    /// </summary>
    private static readonly TimeSpan FinalWriteGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Idle shutdown normally travels cooperatively through TerminalRunner so its PTY and session
    /// records are closed cleanly. If that unwind wedges, this fallback records the run's outcome
    /// and terminates the whole Job process tree. Idle after the final action is the Automation's
    /// completion signal, so that outcome is Succeeded (through the cancel-aware store path); idle
    /// with actions still queued behind the Worker is a Failed run, because those actions can no
    /// longer execute. Once the Worker phase has returned normally the fallback stands down and
    /// leaves completion to <see cref="RunAsync"/>.
    /// </summary>
    private static CancellationTokenRegistration ArmWorkerIdleShutdownFallback(
        IJobStore store,
        string runId,
        CancellationToken idleShutdownToken,
        JobRunShutdownState state,
        bool isLastAction)
    {
        return idleShutdownToken.Register(() =>
        {
            var thread = new Thread(() =>
            {
                Thread.Sleep(FinalWriteGrace);
                if (Volatile.Read(ref state.RunFinalized) != 0)
                    return;
                if (Volatile.Read(ref state.WorkerPhaseComplete) != 0)
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

                var fallbackStatus = JobRunStatus.Failed;
                if (isLastAction)
                {
                    fallbackStatus = RecordIdleSuccess(store, runId);
                }
                else
                {
                    const string failure =
                        "The Worker stopped producing output but its terminal did not close, so remaining workflow actions could not run.";
                    RecordTerminalStatus(store, runId, JobRunStatus.Failed, failure);
                }

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

    /// <summary>
    /// The success twin of <see cref="RecordTerminalStatus"/>: same bounded, swallow-everything
    /// contract, but through the store's atomic Succeeded-or-Cancelled write so a Stop clicked
    /// during the idle grace period is not overwritten. Returns what was recorded (or Succeeded
    /// when the write could not land — the reaper then closes the row as Interrupted).
    /// </summary>
    private static JobRunStatus RecordIdleSuccess(IJobStore store, string runId)
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

    private sealed class JobRunShutdownState
    {
        public int RunFinalized;
        public int OutcomeClaim;
        public int WorkerPhaseComplete;
    }

    private sealed record ActionOutcome(
        JobRunActionStatus ActionStatus,
        JobRunStatus RunStatus,
        int ExitCode,
        string? Error,
        string StandardOutput,
        string StandardError)
    {
        public static ActionOutcome Succeeded { get; } = new(
            JobRunActionStatus.Succeeded,
            JobRunStatus.Succeeded,
            0,
            null,
            string.Empty,
            string.Empty);

        public static ActionOutcome Failed(string error, int exitCode = 1) => new(
            JobRunActionStatus.Failed,
            JobRunStatus.Failed,
            exitCode,
            error,
            string.Empty,
            string.Empty);
    }
}
