using Serilog;
using VibeRails.Utils;

namespace VibeRails.Services;

public interface ILocalClientTracker
{
    void AcquireOwner(string ownerId);
    void PulseOwner(string ownerId, TimeSpan ttl);
    void ReleaseOwner(string ownerId);
    bool HasActiveOwners { get; }
    bool HasActiveOwnersWithPrefix(string ownerIdPrefix);
    string GetDiagnosticsSummary();
}

public sealed class LocalClientTracker : ILocalClientTracker
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _persistentOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _pulseOwners = new(StringComparer.Ordinal);

    public void AcquireOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return;

        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            PruneExpiredLocked(now);
            _persistentOwners.Add(ownerId);
        }
    }

    public void PulseOwner(string ownerId, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return;

        if (ttl <= TimeSpan.Zero)
            return;

        var now = DateTimeOffset.UtcNow;
        var expiresUtc = now.Add(ttl);
        lock (_lock)
        {
            PruneExpiredLocked(now);
            _pulseOwners[ownerId] = expiresUtc;
        }
    }

    public void ReleaseOwner(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return;

        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            PruneExpiredLocked(now);
            _persistentOwners.Remove(ownerId);
            _pulseOwners.Remove(ownerId);
        }
    }

    public bool HasActiveOwners
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            lock (_lock)
            {
                PruneExpiredLocked(now);
                return _persistentOwners.Count > 0 || _pulseOwners.Count > 0;
            }
        }
    }

    public bool HasActiveOwnersWithPrefix(string ownerIdPrefix)
    {
        if (string.IsNullOrEmpty(ownerIdPrefix))
            return false;

        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            PruneExpiredLocked(now);
            return _persistentOwners.Any(x => x.StartsWith(ownerIdPrefix, StringComparison.Ordinal))
                   || _pulseOwners.Keys.Any(x => x.StartsWith(ownerIdPrefix, StringComparison.Ordinal));
        }
    }

    public string GetDiagnosticsSummary()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            PruneExpiredLocked(now);
            return BuildSummaryLocked(now);
        }
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        if (_pulseOwners.Count == 0)
            return;

        var expired = _pulseOwners
            .Where(x => x.Value <= now)
            .Select(x => x.Key)
            .ToArray();

        foreach (var key in expired)
        {
            _pulseOwners.Remove(key);
        }
    }

    private string BuildSummaryLocked(DateTimeOffset now)
    {
        var persistent = _persistentOwners
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var pulse = _pulseOwners
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x =>
            {
                var remaining = x.Value - now;
                var remainingSeconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
                return $"{x.Key}(ttl={remainingSeconds}s)";
            })
            .ToArray();

        var persistentSummary = persistent.Length == 0 ? "none" : string.Join(", ", persistent);
        var pulseSummary = pulse.Length == 0 ? "none" : string.Join(", ", pulse);
        return $"persistent[{persistent.Length}]={persistentSummary}; pulse[{pulse.Length}]={pulseSummary}";
    }
}

public sealed class LocalClientLifecycleWatchdogService(
    ILocalClientTracker localClientTracker,
    IHostApplicationLifetime hostApplicationLifetime) : BackgroundService
{
    private const string BrowserOwnerPrefix = "browser:";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleShutdownTimeout = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // In --env mode, lifetime is controlled by the foreground CLI loop. In plain
        // web-server mode, keep running until Ctrl+C unless the user launched with --web.
        var args = ParserConfigs.GetArguments();
        if (args.IsLMBootstrap || args.IsVsCodeMode || !args.ShutdownOnBrowserClose)
        {
            Log.Information(
                "[Lifecycle] Watchdog disabled for current mode. isLMBootstrap={IsLMBootstrap} isVsCodeMode={IsVsCodeMode} shutdownOnBrowserClose={ShutdownOnBrowserClose} processId={ProcessId}",
                args.IsLMBootstrap,
                args.IsVsCodeMode,
                args.ShutdownOnBrowserClose,
                Environment.ProcessId);
            return;
        }

        var idleStartedUtc = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(CheckInterval);

        Log.Information(
            "[Lifecycle] Watchdog started. processId={ProcessId} checkIntervalSeconds={CheckIntervalSeconds} idleShutdownSeconds={IdleShutdownSeconds}",
            Environment.ProcessId,
            (int)CheckInterval.TotalSeconds,
            (int)IdleShutdownTimeout.TotalSeconds);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (localClientTracker.HasActiveOwnersWithPrefix(BrowserOwnerPrefix))
                {
                    idleStartedUtc = DateTimeOffset.UtcNow;
                    continue;
                }

                if (DateTimeOffset.UtcNow - idleStartedUtc < IdleShutdownTimeout)
                {
                    continue;
                }

                var ownerSummary = localClientTracker.GetDiagnosticsSummary();
                var detail =
                    $"idleSeconds={(int)IdleShutdownTimeout.TotalSeconds}; processId={Environment.ProcessId}; owners={ownerSummary}";
                ShutdownDiagnostics.RecordStopRequest("LocalClientLifecycleWatchdog", detail);
                Log.Warning(
                    "[Lifecycle] No active local browser/terminal owner for {IdleSeconds}s. Stopping process {ProcessId}. owners={OwnerSummary}",
                    (int)IdleShutdownTimeout.TotalSeconds,
                    Environment.ProcessId,
                    ownerSummary);
                hostApplicationLifetime.StopApplication();
                break;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
