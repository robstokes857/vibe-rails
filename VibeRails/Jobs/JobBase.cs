using VibeRails.Services;

namespace VibeRails.Jobs;

public abstract class JobBase : BackgroundService
{
    protected readonly ILogger _logger;
    private readonly ISystemResourceService _resources;

    protected JobBase(ILogger logger, ISystemResourceService resources)
    {
        _logger    = logger;
        _resources = resources;
    }

    protected abstract TimeSpan Interval { get; }
    protected virtual JobPriority Priority => JobPriority.Low;
    protected abstract Task ExecuteJob(CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[{Job}] Starting (interval={Interval}, priority={Priority})",
            GetType().Name, Interval, Priority);

        using var timer = new PeriodicTimer(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // Defer low-to-medium priority jobs when the system is under pressure.
                if (Priority <= JobPriority.Med && _resources.IsUnderPressure)
                {
                    var s = _resources.Current;
                    _logger.LogDebug(
                        "[{Job}] Deferring tick — system under pressure (CPU={Cpu:F0}%, Mem={Mem:F0}%)",
                        GetType().Name, s.ProcessCpuPercent, s.MemoryUsedPercent);
                    continue;
                }

                try
                {
                    await ExecuteJob(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Job}] Unhandled exception", GetType().Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("[{Job}] Stopped", GetType().Name);
    }
}

public enum JobPriority { Lowest, Low, Med, High, Now }
