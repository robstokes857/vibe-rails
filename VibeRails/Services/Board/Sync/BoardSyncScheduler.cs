using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VibeRails.Utils;

namespace VibeRails.Services.Board.Sync;

/// <summary>Starts a sync of every published board about every 60 seconds from the root scheduler that holds the lease.</summary>
public interface IBoardSyncScheduler
{
    /// <summary>
    /// Starts a sync when one is due and none is running, and returns without waiting for it. A
    /// sync is a handful of small HTTP calls, but a slow server must never hold up the Automation
    /// cycle that ticks this. Returns the started sync, or null when nothing was started.
    /// </summary>
    Task? Tick(DateTime nowUtc, CancellationToken stoppingToken);

    /// <summary>Completes when no sync is running. A running sync observes the stopping token.</summary>
    Task WhenIdleAsync();
}

/// <summary>
/// Singleton so the interval survives between scheduler cycles (the Jira pull scheduler's shape).
/// The sync service is scoped, so each run resolves it from a fresh scope. Rob set the interval
/// to 60 s on 2026-09-27: push and pull both ride the same tick.
/// </summary>
public sealed class BoardSyncScheduler(IServiceScopeFactory scopeFactory) : IBoardSyncScheduler
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private readonly Lock _gate = new();
    private DateTime _nextUtc = DateTime.MinValue;
    private Task _running = Task.CompletedTask;

    public Task? Tick(DateTime nowUtc, CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            if (nowUtc < _nextUtc || !_running.IsCompleted)
                return null;
            _nextUtc = nowUtc.Add(Interval);
            return _running = Task.Run(() => SyncAsync(stoppingToken), CancellationToken.None);
        }
    }

    public Task WhenIdleAsync()
    {
        lock (_gate)
            return _running;
    }

    // Never throws: the task is observed only by WhenIdleAsync at shutdown.
    private async Task SyncAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IBoardSyncService>().SyncDueAsync(stoppingToken);
            // Sequentially reuse the OS lock after ordinary sync, so the two never starve each
            // other by racing for it. No extra background host or production listener.
            if (scope.ServiceProvider.GetService<Sharing.CardSharePublisher>() is { } cards)
            {
                try { await cards.RefreshDueAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception) { Log.Warning("[CardSharing] Scheduled refresh failed. Use Refresh shared card to inspect the result."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // SyncDueAsync records per-board failures on each link; anything reaching here is a
            // failure of the sweep itself (lock, store, DI). Log the exception in full so a dead
            // scheduler and a healthy one do not look identical on disk: these messages are the
            // desktop's own wording or runtime text, never a remote body or the API key.
            Log.Warning(ex, "[BoardSync] Scheduled sync failed");
        }
    }
}

/// <summary>
/// The OS file lock that lets one sync run at a time across every VibeRails process on this machine
/// (browser app and VS Code can both be root backends). Released by the OS if the process dies.
/// </summary>
public sealed class BoardSyncLock(string path)
{
    public const string FileName = ".board-sync.lock";

    public static BoardSyncLock BesideStateDatabase() =>
        new(CrossProcessFileLock.BesideStateDatabase(ParserConfigs.GetStatePath(), FileName));

    internal CrossProcessFileLock? TryAcquire() => CrossProcessFileLock.TryAcquire(path);
}
