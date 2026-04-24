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
        var jobName = GetType().Name;
        // NOTE: builder.Logging.ClearProviders() in Program.cs disables ILogger sinks, so
        // we duplicate lifecycle events through Serilog.Log so they reach the log file.
        _logger.LogInformation("[{Job}] Starting (interval={Interval}, priority={Priority})",
            jobName, Interval, Priority);
        Serilog.Log.Information("[Job:{Job}] Starting interval={Interval} priority={Priority} pid={Pid}",
            jobName, Interval, Priority, Environment.ProcessId);

        using var timer = new PeriodicTimer(Interval);
        var tickCount = 0L;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                tickCount++;

                // Defer low-to-medium priority jobs when the system is under pressure.
                if (Priority <= JobPriority.Med && _resources.IsUnderPressure)
                {
                    var s = _resources.Current;
                    _logger.LogDebug(
                        "[{Job}] Deferring tick — system under pressure (CPU={Cpu:F0}%, Mem={Mem:F0}%)",
                        jobName, s.ProcessCpuPercent, s.MemoryUsedPercent);
                    Serilog.Log.Information(
                        "[Job:{Job}] Deferred tick={Tick} reason=pressure cpu={Cpu:F0} mem={Mem:F0}",
                        jobName, tickCount, s.ProcessCpuPercent, s.MemoryUsedPercent);
                    continue;
                }

                var started = DateTime.UtcNow;
                Serilog.Log.Information("[Job:{Job}] Tick begin. tick={Tick} pid={Pid}", jobName, tickCount, Environment.ProcessId);
                try
                {
                    await ExecuteJob(stoppingToken);
                    Serilog.Log.Information(
                        "[Job:{Job}] Tick end. tick={Tick} duration={Duration}",
                        jobName, tickCount, DateTime.UtcNow - started);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    Serilog.Log.Information("[Job:{Job}] Tick cancelled by shutdown. tick={Tick}", jobName, tickCount);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Job}] Unhandled exception", jobName);
                    Serilog.Log.Error(ex,
                        "[Job:{Job}] Unhandled exception in tick={Tick} duration={Duration} pid={Pid}",
                        jobName, tickCount, DateTime.UtcNow - started, Environment.ProcessId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("[{Job}] Stopped", jobName);
        Serilog.Log.Information("[Job:{Job}] Stopped. totalTicks={Tick} pid={Pid}", jobName, tickCount, Environment.ProcessId);
    }
}

public enum JobPriority { Lowest, Low, Med, High, Now }
