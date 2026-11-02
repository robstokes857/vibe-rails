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
    /// <summary>
    /// Delay before the next tick, read after every tick and for the first one. A job with a backlog
    /// overrides it to follow up sooner and returns to <see cref="Interval"/> once it is idle.
    /// </summary>
    protected virtual TimeSpan NextDelay => Interval;
    protected virtual JobPriority Priority => JobPriority.Low;
    /// <summary>
    /// After deferring for pressure this long without running, a Low or Med job runs its next tick anyway.
    /// Pressure is machine-wide CPU (VIBE-172), so a box that is always busy must not stop these jobs for good.
    /// </summary>
    protected virtual TimeSpan MaxDeferral => TimeSpan.FromMinutes(30);
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

        using var timer = new PeriodicTimer(NextDelay);
        var tickCount = 0L;
        DateTime? deferredSince = null;

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                tickCount++;

                // Defer low-to-medium priority jobs when the system is under pressure, up to MaxDeferral.
                if (Priority <= JobPriority.Med && _resources.IsUnderPressure)
                {
                    var s = _resources.Current;
                    deferredSince ??= DateTime.UtcNow;
                    var deferredFor = DateTime.UtcNow - deferredSince.Value;
                    if (deferredFor < MaxDeferral)
                    {
                        _logger.LogDebug(
                            "[{Job}] Deferring tick — system under pressure (CPU={Cpu:F0}%, process={ProcessCpu:F0}%, Mem={Mem:F0}%)",
                            jobName, s.SystemCpuPercent ?? s.ProcessCpuPercent, s.ProcessCpuPercent, s.MemoryUsedPercent);
                        Serilog.Log.Information(
                            "[Job:{Job}] Deferred tick={Tick} reason=pressure systemCpu={SystemCpu:F0} processCpu={Cpu:F0} mem={Mem:F0}",
                            jobName, tickCount, s.SystemCpuPercent, s.ProcessCpuPercent, s.MemoryUsedPercent);
                        // A deferred backlog waits for the normal interval, not the follow-up delay.
                        Reschedule(timer, Interval);
                        continue;
                    }
                    Serilog.Log.Information(
                        "[Job:{Job}] Running under pressure after deferring for {DeferredFor} tick={Tick} systemCpu={SystemCpu:F0} processCpu={Cpu:F0} mem={Mem:F0}",
                        jobName, deferredFor, tickCount, s.SystemCpuPercent, s.ProcessCpuPercent, s.MemoryUsedPercent);
                }
                deferredSince = null;

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
                Reschedule(timer, NextDelay);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("[{Job}] Stopped", jobName);
        Serilog.Log.Information("[Job:{Job}] Stopped. totalTicks={Tick} pid={Pid}", jobName, tickCount, Environment.ProcessId);
    }

    /// <summary>Setting <see cref="PeriodicTimer.Period"/> restarts the wait, so the timer is only touched when the delay changes.</summary>
    private static void Reschedule(PeriodicTimer timer, TimeSpan delay)
    {
        if (timer.Period != delay) timer.Period = delay;
    }
}

public enum JobPriority { Lowest, Low, Med, High, Now }
