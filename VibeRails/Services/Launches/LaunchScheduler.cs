using System.Diagnostics;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.AzureDevOps;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Jira;
using VibeRails.Services.Jobs;
using VibeRails.Services.Terminal;

namespace VibeRails.Services.Launches;

/// <summary>Scheduling only while a root backend is open. Durable claims remain authoritative across roots.</summary>
public sealed class LaunchScheduler(IServiceScopeFactory scopes, ILaunchStore store,
    IJiraPullScheduler? jira = null, IAdoPullScheduler? ado = null, IBoardSyncScheduler? sync = null) : BackgroundService
{
    private readonly string owner = Environment.ProcessId + ":" + Guid.NewGuid().ToString("N");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { Serilog.Log.Error(ex, "[Launches] Scheduler tick failed; durable work remains queued"); }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            await LaunchActionTasks.StopAsync();
            if (jira is not null) await jira.WhenIdleAsync();
            if (ado is not null) await ado.WhenIdleAsync();
            if (sync is not null) await sync.WhenIdleAsync();
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var launcher = services.GetRequiredService<LaunchDispatcher>();
        await services.GetRequiredService<ITerminalTabHostService>().CloseCompletedAutomationTabsAsync(ct);
        var launch = services.GetRequiredService<LaunchService>();
        var now = DateTime.UtcNow;
        var project = await services.GetRequiredService<IBoardProjectResolver>().ResolveAsync(ct);
        await store.TryLeaseProjectAsync(LaunchService.ProjectKey(project), LaunchService.ProjectRoot(project), owner, now, TimeSpan.FromSeconds(30), ct);
        await services.GetRequiredService<BoardLaunchWorkflow>().TickAsync(ct);
        if (await store.TryLeaseAsync("scheduler", owner, now, TimeSpan.FromSeconds(30), ct))
        {
            jira?.Tick(now, ct);
            ado?.Tick(now, ct);
            sync?.Tick(now, ct);
            foreach (var binding in await store.ListBindingsAsync(ct: ct))
            {
                if (!binding.Enabled || binding.NextRunUtc is not { } due || due > now) continue;
                try
                {
                    // The binding's cased root, when it has one, is also the working directory (VIBE-296).
                    var bindingRoot = binding.ProjectRoot ?? binding.ProjectPath;
                    await launch.EnqueueAsync(binding.DefinitionType, binding.DefinitionId,
                        new(bindingRoot, bindingRoot, "schedule", $"schedule:{binding.Id}:{due:O}", binding.Id), ct);
                    await store.AdvanceBindingAsync(binding.Id, binding.Revision, due, JobScheduleCalculator.ComputeNext(binding.Trigger, now), ct);
                }
                catch (Exception ex) when (ex is LaunchValidationException or LaunchConflictException or AutomationScriptValidationException)
                {
                    // Keep the binding visible and due; a user can repair the definition/approval.
                    Serilog.Log.Warning("[Launches] Binding {BindingId} is ineligible: {Reason}", binding.Id, ex.Message);
                }
            }
        }
        var openProjects = await store.GetOpenProjectRootsAsync(now, ct);
        var runs = await store.GetActiveRunsAsync(ct);
        foreach (var run in runs)
        {
            if (run.Status == "queued" && run.Snapshot.Context.TriggerKind == "board")
            {
                if (run.QueuedUtc < now.AddMinutes(-1)) await store.CancelAsync(run.Id, run.ProjectPath, ct);
            }
            else if (run.Status == "queued")
            {
                if (CanDispatchHere(run, project, openProjects, now)) await launcher.DispatchAsync(run.Id, ct);
            }
            else await ReconcileAsync(run, services, ct);
        }
    }

    /// <summary>Keep terminals in their project's open root; allow closed-project schedules after a grace period.</summary>
    internal static bool CanDispatchHere(LaunchRun run, string project, IReadOnlyList<string> openProjects, DateTime now) =>
        Utils.ProjectPathComparer.Matches(project, run.ProjectPath)
        || run.QueuedUtc <= now.AddSeconds(-30) && !openProjects.Any(open => Utils.ProjectPathComparer.Matches(open, run.ProjectPath));

    internal async Task ReconcileAsync(LaunchRun run, IServiceProvider services, CancellationToken ct)
    {
        if (run.ClaimToken is null) return;
        var claim = new LaunchClaim(run.Id, run.ClaimToken);
        // An uncertain, unstarted preset is sealed: StartAsync cannot claim it, and all CLI
        // spawning happens after StartAsync. It is safe to release even if the root is alive.
        if (run.Status == "uncertain" && run.Snapshot.Preset is not null && run.SessionId is null)
        {
            await store.CompleteAsync(claim, "interrupted", error: "Launch was sealed before a CLI session started. Retry explicitly.", ct: ct);
            return;
        }
        if (run.SessionId is not null)
        {
            var session = await services.GetRequiredService<IRepository>().GetSessionByIdAsync(run.SessionId, ct);
            if (session?.EndedUTC is not null)
            {
                await store.CompleteAsync(claim, run.DeadlineUtc is { } deadline && deadline <= session.EndedUTC ? "timed-out"
                    : session.ExitCode == 0 ? "succeeded" : "failed", session.ExitCode, ct: ct);
                return;
            }
        }
        // A process identity includes its creation time. Unknown/access-denied is not dead.
        var alive = ProcessAlive(run.OwnerPid, run.OwnerStartedUtc);
        if (alive != false) return;
        if (run.Snapshot.Action?.Script is not null && run.StartedUtc is not null)
        {
            if (run.ExecutionPid is null)
            {
                // The owner may have died between the OS spawn and its durable acknowledgment.
                // Keep capacity reserved; never convert this ambiguity into a blind retry.
                await store.CompleteAsync(claim, "uncertain", error: "The script owner ended before its process identity was recorded. Inspect the retained recording and running processes; no automatic retry.", ct: ct);
                return;
            }
            var childAlive = ProcessAlive(run.ExecutionPid, run.ExecutionStartedUtc);
            if (childAlive != false)
            {
                if (childAlive == true && (run.CancelRequested || run.DeadlineUtc <= DateTime.UtcNow))
                {
                    try
                    {
                        using var child = Process.GetProcessById(run.ExecutionPid.Value);
                        if (child.StartTime.ToUniversalTime() == run.ExecutionStartedUtc!.Value)
                            child.Kill(entireProcessTree: true);
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
                return;
            }
        }
        // Once StartAsync has recorded the child, OwnerPid names that child. Before then it
        // names the root. An unacknowledged child can still be starting: seal the claim as
        // uncertain before deciding anything else, so it cannot begin executing afterward.
        if (run.Status == "claimed")
            await store.CompleteAsync(claim, "uncertain", error: "Launch owner ended before acknowledgement. No automatic retry.", ct: ct);
        else
            await store.CompleteAsync(claim, "interrupted", error: "The recorded execution owner ended. Retry explicitly after inspecting retained evidence.", ct: ct);
    }

    internal static bool? ProcessAlive(int? pid, DateTime? started)
    {
        if (pid is null || started is null) return null;
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            return !process.HasExited && process.StartTime.ToUniversalTime() == started.Value;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
