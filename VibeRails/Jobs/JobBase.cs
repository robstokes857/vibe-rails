using VibeRails.Services;

namespace VibeRails.Jobs;

public abstract class JobBase : BackgroundService
{
    protected readonly ILogger _logger;
    private readonly ISystemResourceService _resources;
    private readonly TimeProvider _clock;
    private long _tickCount;
    private DateTimeOffset? _cpuPressureSince;
    private bool _cpuOverrideLogged;

    protected JobBase(ILogger logger, ISystemResourceService resources, TimeProvider? clock = null)
    {
        _logger    = logger;
        _resources = resources;
        _clock     = clock ?? TimeProvider.System;
    }

    protected abstract TimeSpan Interval { get; }
    /// <summary>
    /// Delay before the next tick, read after every tick and for the first one. A job with a backlog
    /// overrides it to follow up sooner and returns to <see cref="Interval"/> once it is idle.
    /// </summary>
    protected virtual TimeSpan NextDelay => Interval;
    protected virtual JobPriority Priority => JobPriority.Low;
    /// <summary>
    /// The longest one episode of CPU pressure defers a Low or Med job. CPU pressure is machine-wide
    /// (VIBE-172), so once an episode has lasted this long the job runs at its normal cadence until a
    /// tick sees the machine below the threshold. Without a cap, a box that is always busy would stop
    /// these jobs for good. Memory pressure is not capped.
    /// </summary>
    protected virtual TimeSpan MaxCpuDeferral => TimeSpan.FromMinutes(30);
    /// <summary>
    /// False for a job that applies its own CPU policy, so the shared deferral does not stack on top of it
    /// (<see cref="BertEmbeddingBackfillJob"/>). Memory pressure still defers it.
    /// </summary>
    protected virtual bool DefersForCpuPressure => true;
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
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Reschedule(timer, await RunTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation("[{Job}] Stopped", jobName);
        Serilog.Log.Information("[Job:{Job}] Stopped. totalTicks={Tick} pid={Pid}", jobName, _tickCount, Environment.ProcessId);
    }

    /// <summary>
    /// One scheduled tick: the pressure gate, then the job. Returns the delay before the next tick. The
    /// poll loop is its only caller, so it never runs concurrently.
    /// </summary>
    internal async Task<TimeSpan> RunTickAsync(CancellationToken stoppingToken)
    {
        var jobName = GetType().Name;
        var tick = ++_tickCount;

        // A deferred backlog waits for the normal interval, not the follow-up delay.
        if (Priority <= JobPriority.Med && ShouldDefer(jobName, tick)) return Interval;

        var started = DateTime.UtcNow;
        Serilog.Log.Information("[Job:{Job}] Tick begin. tick={Tick} pid={Pid}", jobName, tick, Environment.ProcessId);
        try
        {
            await ExecuteJob(stoppingToken);
            Serilog.Log.Information(
                "[Job:{Job}] Tick end. tick={Tick} duration={Duration}",
                jobName, tick, DateTime.UtcNow - started);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            Serilog.Log.Information("[Job:{Job}] Tick cancelled by shutdown. tick={Tick}", jobName, tick);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Job}] Unhandled exception", jobName);
            Serilog.Log.Error(ex,
                "[Job:{Job}] Unhandled exception in tick={Tick} duration={Duration} pid={Pid}",
                jobName, tick, DateTime.UtcNow - started, Environment.ProcessId);
        }
        return NextDelay;
    }

    /// <summary>
    /// Memory pressure always defers. CPU pressure defers until its episode has lasted
    /// <see cref="MaxCpuDeferral"/>; the episode ends at the first tick that sees no CPU pressure.
    /// </summary>
    private bool ShouldDefer(string jobName, long tick)
    {
        var memory = _resources.IsMemoryUnderPressure;
        var cpu = DefersForCpuPressure && _resources.IsCpuUnderPressure;
        var now = _clock.GetUtcNow();
        if (cpu) _cpuPressureSince ??= now;
        else
        {
            _cpuPressureSince = null;
            _cpuOverrideLogged = false;
        }
        var cpuFor = now - (_cpuPressureSince ?? now);
        var deferForCpu = cpu && cpuFor < MaxCpuDeferral;
        var s = _resources.Current;
        if (memory || deferForCpu)
        {
            var reason = memory ? "memory" : "cpu";
            _logger.LogDebug(
                "[{Job}] Deferring tick — system under {Reason} pressure (CPU={Cpu:F0}%, process={ProcessCpu:F0}%, Mem={Mem:F0}%)",
                jobName, reason, s.SystemCpuPercent ?? s.ProcessCpuPercent, s.ProcessCpuPercent, s.MemoryUsedPercent);
            Serilog.Log.Information(
                "[Job:{Job}] Deferred tick={Tick} reason={Reason} systemCpu={SystemCpu:F0} processCpu={Cpu:F0} mem={Mem:F0}",
                jobName, tick, reason, s.SystemCpuPercent, s.ProcessCpuPercent, s.MemoryUsedPercent);
            return true;
        }
        if (cpu && !_cpuOverrideLogged)
        {
            _cpuOverrideLogged = true;
            Serilog.Log.Information(
                "[Job:{Job}] Running despite CPU pressure after deferring for {DeferredFor} tick={Tick} systemCpu={SystemCpu:F0} processCpu={Cpu:F0}",
                jobName, cpuFor, tick, s.SystemCpuPercent, s.ProcessCpuPercent);
        }
        return false;
    }

    /// <summary>Setting <see cref="PeriodicTimer.Period"/> restarts the wait, so the timer is only touched when the delay changes.</summary>
    private static void Reschedule(PeriodicTimer timer, TimeSpan delay)
    {
        if (timer.Period != delay) timer.Period = delay;
    }
}

public enum JobPriority { Lowest, Low, Med, High, Now }
