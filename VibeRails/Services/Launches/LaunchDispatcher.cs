using System.Diagnostics;
using System.Text;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.Board;
using VibeRails.Services.Cli;
using VibeRails.Services.Jobs;
using VibeRails.Services.Terminal;

namespace VibeRails.Services.Launches;

/// <summary>Claims capacity once and executes one preset or one typed Action, without constructing a Job.</summary>
public sealed class LaunchDispatcher(ILaunchStore store, LaunchService service, ITerminalTabHostService tabs,
    IServiceScopeFactory scopes, IBoardStore board)
{
    public const int Capacity = 3;

    public async Task<LaunchRun> DispatchAsync(string id, CancellationToken ct = default)
    {
        var run = await store.GetRunAsync(id, ct) ?? throw new LaunchValidationException("Run not found.");
        if (run.Snapshot.Context.LaneEntryId is { } entryId)
        {
            var entry = (await board.GetLaunchEventsAsync(run.ProjectPath, run.Snapshot.Context.CardId, ct)).SingleOrDefault(e => e.Id == entryId);
            if (entry is { Current: true, SkipRequested: false } && entry.RunId != run.Id)
            {
                if (run.SourceRunId is not null && entry.RunId == run.SourceRunId)
                    await board.RetryLaunchEventAsync(entryId, run.SourceRunId, run.Id, ct);
                return run; // Wait for the separate Board acknowledgment, including crash recovery.
            }
        }
        using var process = Process.GetCurrentProcess();
        var claim = await store.TryClaimAsync(id, Capacity, Environment.ProcessId, process.StartTime.ToUniversalTime(), ct);
        if (claim is null) return (await store.GetRunAsync(id, ct))!;
        try
        {
            await service.ValidateSnapshotAsync(run.Snapshot, ct);
            await LaunchBoardValidation.ValidateAsync(board, run, ct);
        }
        catch (Exception ex) when (ex is LaunchValidationException or AutomationScriptValidationException or BoardValidationException)
        {
            await store.CompleteAsync(claim, "failed", error: ex.Message, ct: CancellationToken.None);
            return (await store.GetRunAsync(id, CancellationToken.None))!;
        }
        catch (Exception ex)
        {
            // Nothing has been handed to a terminal or Action yet. A cancelled request
            // or failed validation must not strand its durable capacity reservation.
            await store.CompleteAsync(claim, ex is OperationCanceledException ? "cancelled" : "failed",
                error: ex is OperationCanceledException ? null : "Launch validation could not complete.",
                ct: CancellationToken.None);
            throw;
        }
        if (run.Snapshot.Action is not null)
        {
            // The new scope lives for the whole action. The host tracks this task and cancels/joins
            // it on shutdown; the request scope is never retained by fire-and-forget execution.
            LaunchActionTasks.Start(id, token => ExecuteActionAsync(scopes, run, claim, token));
            return (await store.GetRunAsync(id, ct))!;
        }
        TerminalTabStatusResponse? tab = null;
        try
        {
            tab = await tabs.CreateTabAsync(ct);
            if (run.Snapshot.Context.TriggerKind is not ("manual" or "board"))
                tabs.RegisterLaunchTab(tab.TabId, run.Id, run.Snapshot.Preset!.Name);
            if (!await store.SetTabAsync(claim, tab.TabId, ct))
            {
                await tabs.DeleteTabAsync(tab.TabId, CancellationToken.None);
                await store.CompleteAsync(claim, "cancelled");
                return (await store.GetRunAsync(id, ct))!;
            }
            var preset = run.Snapshot.Preset!;
            var context = run.Snapshot.Context;
            // A Board launch supplies the card's `{DisplayId} · {Title}`; the tab and the card's one
            // session link both use it. The child reads the same title from the snapshot (VIBE-296).
            var title = context.SessionTitle ?? preset.Name;
            var session = await tabs.StartSessionAsync(tab.TabId, new StartTerminalRequest(
                WorkingDirectory: context.WorkingDirectory, Cli: LlmParser.ToWireName(preset.Cli), Title: title,
                ContextIncluded: context.ContextIncluded) { LaunchClaim = claim, PreserveWorkingDirectory = true }, ct);
            if (context.CardId is not null && session.SessionId is not null)
            {
                var origin = context.SessionOrigin ?? BoardSessionRecord.LaunchOrigin;
                try
                {
                    await board.LinkSessionAsync(context.Root, context.CardId, session.SessionId, tab.TabId,
                        context.Selection ?? $"preset:{preset.Id}:{LlmParser.ToWireName(preset.Cli).ToLowerInvariant()}",
                        LlmParser.ToWireName(preset.Cli), title, origin, ct);
                }
                catch (BoardConflictException) when (ConfirmsLaunchOrigin(origin))
                {
                    // The CLI's own MCP connection can attach its session first (origin "mcp"). This
                    // launch then refines that one attachment's origin rather than linking it twice.
                    if (!await board.ConfirmSessionLaunchOriginAsync(context.Root, context.CardId, session.SessionId, origin, ct))
                        throw;
                }
            }
        }
        catch (Exception ex)
        {
            // A timed-out HTTP handshake may already have spawned the CLI. Never requeue it.
            // StartAsync is forbidden after this transition; a session already started wins.
            await store.CompleteAsync(claim, tab is null ? "failed" : "uncertain",
                error: tab is null ? "The terminal could not be created." : "The terminal launch acknowledgement is uncertain. Reconciliation is required.");
            Serilog.Log.Warning(ex, "[Launches] Launch acknowledgement failed for {RunId}", id);
        }
        return (await store.GetRunAsync(id, CancellationToken.None))!;
    }

    /// <summary>The explicit launch origins <see cref="IBoardStore.ConfirmSessionLaunchOriginAsync"/> may apply to an early attachment.</summary>
    private static bool ConfirmsLaunchOrigin(string origin) => origin is BoardSessionRecord.LaunchOrigin
        or BoardSessionRecord.ManagerOrigin or BoardSessionRecord.WorkerOrigin or BoardSessionRecord.PlanningWorkerOrigin
        or "chat" or "planning" or "code_review" or BoardSessionRecord.AutomationOrigin or "lane_review";

    private static async Task ExecuteActionAsync(IServiceScopeFactory scopes, LaunchRun run, LaunchClaim claim, CancellationToken stopping)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var store = services.GetRequiredService<ILaunchStore>();
        var validator = services.GetRequiredService<LaunchService>();
        var repository = services.GetRequiredService<IRepository>();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var action = run.Snapshot.Action!;
        string? sessionId = null;
        SessionOutputWriter? writer = null;
        Task? watcher = null;
        var exitCode = 1;
        try
        {
            await validator.ValidateSnapshotAsync(run.Snapshot, cancel.Token);
            await LaunchBoardValidation.ValidateAsync(services.GetRequiredService<IBoardStore>(), run, cancel.Token);
            using var process = Process.GetCurrentProcess();
            var proposedSessionId = Guid.NewGuid().ToString();
            var deadline = action.Script?.TimeoutSeconds is { } secondsLimit ? DateTime.UtcNow.AddSeconds(secondsLimit) : (DateTime?)null;
            if (!await store.StartAsync(claim, proposedSessionId, null, run.Snapshot.Context.WorkingDirectory,
                Environment.ProcessId, process.StartTime.ToUniversalTime(), deadline, cancel.Token))
            {
                // Cancellation can arrive after the claim but before the durable start.
                // No Action has executed: acknowledge it using this claim's ownership token.
                await store.CompleteAsync(claim, "cancelled", ct: CancellationToken.None);
                return;
            }
            sessionId = proposedSessionId;
            await repository.CreateSessionAsync(sessionId, "Shell", null, run.Snapshot.Context.WorkingDirectory, Environment.ProcessId);
            await repository.SetSessionDisplayNameAsync(sessionId, "Action: " + action.Name);
            writer = new SessionOutputWriter(repository);
            writer.Initialize(sessionId);
            watcher = WatchCancellationAsync(store, run.Id, cancel);
            string status;
            string? evidence = null;
            string? verdict = null;
            string? error = null;
            if (action.Script is { } script)
            {
                // Adapter only for the established script validator. No Job/run-action is persisted or executed.
                var scriptInput = new JobRunActionRecord(action.Id, run.Id, null, 0, JobActionKind.Script,
                    JobRunActionStatus.Pending, null, null, LLM.NotSet, script.Path, script.Runtime,
                    script.Arguments, script.WorkingDirectory, script.TimeoutSeconds, run.Snapshot.ApprovedScriptHash,
                    null, null, null, null, null, "", "");
                var prepared = await services.GetRequiredService<IAutomationScriptService>().PrepareAsync(
                    run.Snapshot.Context.WorkingDirectory, run.Snapshot.Context.Root, scriptInput, cancel.Token);
                var result = await services.GetRequiredService<ICliWrapper>().RunAsync(new CliRequest(prepared.Executable,
                    prepared.Arguments, prepared.WorkingDirectory, prepared.EnvironmentVariables,
                    script.TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan,
                    OnStarted: async pid =>
                    {
                        DateTime started;
                        try
                        {
                            using var child = Process.GetProcessById(pid);
                            started = child.StartTime.ToUniversalTime();
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                        { return; } // Already exited; the wrapper will record its outcome.
                        if (!await store.RecordProcessAsync(claim, pid, started, cancel.Token))
                            throw new LaunchConflictException("The script no longer owns this run.");
                    }), line =>
                    {
                        writer.EnqueueLine(Encoding.UTF8.GetBytes(line.Text + "\r\n"), line.IsError, DateTime.UtcNow);
                        return ValueTask.CompletedTask;
                    }, cancel.Token);
                exitCode = result.ExitCode;
                status = result.Cancelled ? "cancelled" : result.TimedOut ? "timed-out" : result.IsSuccess ? "succeeded" : "failed";
                if (status == "failed") error = "Script failed. Open the linked session recording for output.";
            }
            else
            {
                var check = await services.GetRequiredService<BoardCheckService>().ExecuteAsync(run.Snapshot.Context.Root,
                    run.Snapshot.Context.WorkingDirectory, run.Snapshot.Context.CardId, run.Id, action.Id,
                    action.Kind == "vca" ? JobActionKind.Vca : JobActionKind.CodeQuality,
                    LaunchService.CheckArguments(action.Check!), cancel.Token);
                evidence = check.Id;
                verdict = check.FailsAction ? "failed" : "passed";
                exitCode = check.FailsAction ? 1 : 0;
                status = cancel.IsCancellationRequested ? "cancelled" : check.FailsAction ? "failed" : "succeeded";
                await store.SaveCheckEvidenceAsync(run.Id, check, CancellationToken.None);
                writer.EnqueueLine(Encoding.UTF8.GetBytes(check.Summary + "\r\n"), check.FailsAction, DateTime.UtcNow);
            }
            await writer.DisposeAsync();
            writer = null;
            await repository.CompleteSessionAsync(sessionId, exitCode);
            sessionId = null;
            await store.CompleteAsync(claim, status, exitCode, error, evidence, verdict);
        }
        catch (Exception ex)
        {
            if (writer is not null) writer.EnqueueLine(Encoding.UTF8.GetBytes("Action stopped before completion.\r\n"), true, DateTime.UtcNow);
            await store.CompleteAsync(claim, cancel.IsCancellationRequested ? "cancelled" : "failed", 1,
                ex is LaunchValidationException or AutomationScriptValidationException ? ex.Message : "Action could not complete.");
            Serilog.Log.Warning(ex, "[Launches] Action failed for {RunId}", run.Id);
        }
        finally
        {
            cancel.Cancel();
            if (watcher is not null) await watcher;
            if (writer is not null) await writer.DisposeAsync();
            if (sessionId is not null) await repository.CompleteSessionAsync(sessionId, exitCode);
        }
    }

    private static async Task WatchCancellationAsync(ILaunchStore store, string id, CancellationTokenSource cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, cancel.Token);
                if ((await store.GetRunAsync(id, cancel.Token))?.CancelRequested == true) cancel.Cancel();
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
            catch (Exception ex) { Serilog.Log.Warning(ex, "[Launches] Retrying Action cancellation monitor {RunId}", id); }
        }
    }
}

/// <summary>Tracks root-owned Action tasks so application shutdown never abandons live scripts.</summary>
internal static class LaunchActionTasks
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Task Task, CancellationTokenSource Cancel)> Active = new();
    internal static void Start(string id, Func<CancellationToken, Task> execute)
    {
        var cancel = new CancellationTokenSource();
        var task = execute(cancel.Token);
        Active[id] = (task, cancel);
        _ = ObserveAsync(id, task, cancel);
    }
    private static async Task ObserveAsync(string id, Task task, CancellationTokenSource cancel)
    {
        try { await task; }
        catch (Exception ex) { Serilog.Log.Error(ex, "[Launches] Action finalization failed for {RunId}", id); }
        finally { Active.TryRemove(id, out _); cancel.Dispose(); }
    }
    internal static async Task StopAsync()
    {
        var active = Active.Values.ToArray();
        foreach (var entry in active) { try { entry.Cancel.Cancel(); } catch (ObjectDisposedException) { } }
        try { await Task.WhenAll(active.Select(entry => entry.Task)); } catch { /* Observed per task. */ }
    }
}
