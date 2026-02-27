using Serilog;
using VibeRails.Utils;

namespace VibeRails.Services;

public interface ILocalClientTracker
{
    void AcquireOwner(string ownerId);
    void PulseOwner(string ownerId, TimeSpan ttl);
    void ReleaseOwner(string ownerId);
    bool HasActiveOwners { get; }
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
}

public sealed class LocalClientLifecycleWatchdogService(
    ILocalClientTracker localClientTracker,
    IHostApplicationLifetime hostApplicationLifetime) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdleShutdownTimeout = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // In --env mode, lifetime is controlled by the foreground CLI loop.
        if (ParserConfigs.GetArguments().IsLMBootstrap)
        {
            return;
        }

        var idleStartedUtc = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(CheckInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (localClientTracker.HasActiveOwners)
                {
                    idleStartedUtc = DateTimeOffset.UtcNow;
                    continue;
                }

                if (DateTimeOffset.UtcNow - idleStartedUtc < IdleShutdownTimeout)
                {
                    continue;
                }

                Log.Warning(
                    "[Lifecycle] No active local browser/terminal owner for {IdleSeconds}s. Stopping process {ProcessId}.",
                    (int)IdleShutdownTimeout.TotalSeconds,
                    Environment.ProcessId);
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
