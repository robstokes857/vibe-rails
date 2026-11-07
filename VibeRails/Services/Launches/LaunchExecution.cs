using System.Collections.Concurrent;
using System.Diagnostics;
using VibeRails.Launches;

namespace VibeRails.Services.Launches;

/// <summary>The executing process owns prompt resolution, its exact session and cooperative termination.</summary>
public sealed class LaunchExecution(ILaunchStore store, LaunchService service, LaunchRun run, VibeRails.Services.Board.IBoardStore? board = null)
{
    private readonly ILaunchStore launchStore = store;
    private static readonly ConcurrentDictionary<string, LaunchExecution> Sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource finished = new();
    private bool timedOut;
    public LaunchRun Run { get; } = run;
    public LaunchClaim Claim => new(Run.Id, Run.ClaimToken!);

    public async Task BeforeSpawnAsync(string sessionId, string? prompt, string directory, CancellationToken ct)
    {
        await service.ValidateSnapshotAsync(Run.Snapshot, ct);
        LaunchValidation.RejectRetiredPrompt(prompt);
        if (board is not null) await LaunchBoardValidation.ValidateAsync(board, Run, ct);
        using var process = Process.GetCurrentProcess();
        var deadline = Run.Snapshot.Preset?.TimeoutMinutes is { } minutes ? DateTime.UtcNow.AddMinutes(minutes) : (DateTime?)null;
        if (!await launchStore.StartAsync(Claim, sessionId, prompt, directory, Environment.ProcessId, process.StartTime.ToUniversalTime(), deadline, ct))
            throw new LaunchConflictException("This run was cancelled or another process owns it.");
        Sessions[sessionId] = this;
    }

    public void Watch(Func<Task> stopExactSession) => _ = WatchAsync(stopExactSession);
    private async Task WatchAsync(Func<Task> stopExactSession)
    {
        while (!finished.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), finished.Token);
                var current = await launchStore.GetRunAsync(Run.Id, finished.Token);
                if (current is null || current.IsTerminal) return;
                timedOut = current.DeadlineUtc is { } deadline && deadline <= DateTime.UtcNow;
                if (current.CancelRequested || timedOut) { await stopExactSession(); return; }
            }
            catch (OperationCanceledException) when (finished.IsCancellationRequested) { return; }
            catch (Exception ex) { Serilog.Log.Warning(ex, "[Launches] Monitor will retry for {RunId}", Run.Id); }
        }
    }

    public static async Task CompleteSessionAsync(string sessionId, int exitCode)
    {
        if (!Sessions.TryGetValue(sessionId, out var execution)) return;
        var status = execution.timedOut ? "timed-out" : exitCode == 0 ? "succeeded" : "failed";
        await execution.launchStore.CompleteAsync(execution.Claim, status, exitCode);
        Sessions.TryRemove(sessionId, out _);
        execution.finished.Cancel();
    }
}
