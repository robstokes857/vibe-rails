using System.Diagnostics;

namespace VibeRails.Services;

public sealed record ResourceSnapshot
{
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>Our process CPU usage normalized across all cores (0–100).</summary>
    public double ProcessCpuPercent { get; init; }

    /// <summary>
    /// CPU in use across the whole machine, every process and core (0–100). Null where the platform
    /// gives no system-wide figure, and until the first poll after startup.
    /// </summary>
    public double? SystemCpuPercent { get; init; }

    /// <summary>CPU used by everything except this process, so its own background work does not count against it.</summary>
    public double? OtherCpuPercent => SystemCpuPercent is { } system ? Math.Max(0, system - ProcessCpuPercent) : null;

    /// <summary>
    /// How long, as of <see cref="CapturedAt"/>, <see cref="OtherCpuPercent"/> has stayed under
    /// <see cref="SystemResourceService.QuietCpuThreshold"/>. Zero while the machine is busy and in the
    /// startup snapshot. Where the platform has no system-wide figure (macOS) nothing can show the
    /// rest of the machine busy, so polls count as quiet: otherwise a large search backfill could
    /// never pass its idle gate there and always waited out the gate's maximum (VIBE-284).
    /// </summary>
    public TimeSpan QuietFor { get; init; }

    /// <summary>Time since the user's last keyboard or mouse input; Windows only, null elsewhere.</summary>
    public TimeSpan? UserIdleFor { get; init; }

    /// <summary>Total physical memory available to the runtime (from GCMemoryInfo).</summary>
    public long TotalMemoryBytes { get; init; }

    /// <summary>System memory currently in use (from GCMemoryInfo).</summary>
    public long UsedMemoryBytes { get; init; }

    public long FreeMemoryBytes => Math.Max(0, TotalMemoryBytes - UsedMemoryBytes);

    /// <summary>This process's working set.</summary>
    public long ProcessMemoryBytes { get; init; }

    public double MemoryUsedPercent => TotalMemoryBytes > 0
        ? (double)UsedMemoryBytes / TotalMemoryBytes * 100 : 0;
    public double MemoryFreePercent => Math.Max(0, 100 - MemoryUsedPercent);
}

public interface ISystemResourceService
{
    /// <summary>Snapshot captured when the service started.</summary>
    ResourceSnapshot Startup { get; }

    /// <summary>Most recent snapshot (refreshes every 10 s).</summary>
    ResourceSnapshot Current { get; }

    /// <summary>
    /// True when machine-wide CPU is above 85%, or this process's own CPU is above 70% where the platform
    /// has no system-wide figure.
    /// </summary>
    bool IsCpuUnderPressure { get; }

    /// <summary>True when system memory load reaches the GC's built-in high-memory threshold.</summary>
    bool IsMemoryUnderPressure { get; }
}

/// <summary>
/// Polls CPU and memory every 10 s for the resource-aware jobs. Machine-wide CPU (VIBE-172) lets a
/// build, a CLI or another root defer background work; before that only vb.exe's own CPU counted,
/// so on 32 cores this process needed about 22 busy cores before anything deferred.
/// </summary>
public sealed class SystemResourceService : BackgroundService, ISystemResourceService
{
    /// <summary>Machine-wide CPU above this defers Low and Med jobs: the box is saturated, not merely busy.</summary>
    internal const double SystemCpuPressureThreshold = 85.0;
    /// <summary>The original process-only threshold, kept where the platform has no system-wide figure.</summary>
    internal const double ProcessCpuPressureThreshold = 70.0;
    /// <summary>The rest of the machine below this counts toward <see cref="ResourceSnapshot.QuietFor"/>.</summary>
    internal const double QuietCpuThreshold = 25.0;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly Func<ResourceSample> _sample;
    private ResourceSnapshot _current;
    private ResourceSample _lastSample;
    private DateTimeOffset? _quietSince;

    public ResourceSnapshot Startup { get; }
    public ResourceSnapshot Current => Volatile.Read(ref _current);

    public bool IsCpuUnderPressure => ExceedsCpuThreshold(Current);

    public bool IsMemoryUnderPressure
    {
        get
        {
            var gcInfo = GC.GetGCMemoryInfo();
            return gcInfo.MemoryLoadBytes >= gcInfo.HighMemoryLoadThresholdBytes;
        }
    }

    /// <param name="systemWide">
    /// False keeps the original process-only sampling: terminal tab children run no jobs and read
    /// neither the machine counters nor user input.
    /// </param>
    public SystemResourceService(bool systemWide) : this(systemWide ? ResourceSample.Machine : ResourceSample.ProcessOnly) { }

    internal SystemResourceService(Func<ResourceSample> sample)
    {
        _sample = sample;
        _lastSample = sample();
        // CPU starts at 0 — first real reading arrives after one PollInterval tick.
        var snap = BuildSnapshot(_lastSample, procCpu: 0, systemCpu: null, TimeSpan.Zero);
        Startup = snap;
        _current = snap;
    }

    /// <summary>The rule behind <see cref="IsCpuUnderPressure"/>, for one snapshot.</summary>
    internal static bool ExceedsCpuThreshold(ResourceSnapshot snapshot) => snapshot.SystemCpuPercent is { } system
        ? system > SystemCpuPressureThreshold
        : snapshot.ProcessCpuPercent > ProcessCpuPressureThreshold;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                Record(_sample());
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Folds one poll into <see cref="Current"/>. Called by the poll loop only, never concurrently.</summary>
    internal ResourceSnapshot Record(ResourceSample sample)
    {
        var procCpu = ComputeProcessCpu(_lastSample, sample);
        var systemCpu = SystemActivityReader.CpuPercent(_lastSample.System, sample.System);
        var snap = BuildSnapshot(sample, procCpu, systemCpu, TimeSpan.Zero);
        // A reading is the average since the previous poll, so a quiet reading makes that whole interval
        // quiet. No machine figure is no evidence of load: see ResourceSnapshot.QuietFor.
        _quietSince = snap.OtherCpuPercent is not { } other || other < QuietCpuThreshold ? _quietSince ?? _lastSample.At : null;
        if (_quietSince is { } since) snap = snap with { QuietFor = sample.At - since };
        _lastSample = sample;
        Volatile.Write(ref _current, snap);
        return snap;
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static ResourceSnapshot BuildSnapshot(ResourceSample sample, double procCpu, double? systemCpu, TimeSpan quietFor)
    {
        var gcInfo = GC.GetGCMemoryInfo();
        return new ResourceSnapshot
        {
            CapturedAt         = sample.At,
            ProcessCpuPercent  = Math.Round(procCpu, 1),
            SystemCpuPercent   = systemCpu is { } system ? Math.Round(system, 1) : null,
            QuietFor           = quietFor,
            UserIdleFor        = sample.UserIdleFor,
            TotalMemoryBytes   = gcInfo.TotalAvailableMemoryBytes,
            UsedMemoryBytes    = gcInfo.MemoryLoadBytes,
            ProcessMemoryBytes = Environment.WorkingSet,
        };
    }

    private static double ComputeProcessCpu(ResourceSample prev, ResourceSample curr)
    {
        var wallTicks = (curr.At - prev.At).Ticks;
        if (wallTicks <= 0) return 0;
        var cpuTicks = (curr.ProcessorTime - prev.ProcessorTime).Ticks;
        return Math.Clamp(
            (double)cpuTicks / (wallTicks * Environment.ProcessorCount) * 100,
            0, 100);
    }
}

/// <summary>One poll's raw counters.</summary>
internal readonly record struct ResourceSample(DateTimeOffset At, TimeSpan ProcessorTime,
    SystemCpuTimes? System = null, TimeSpan? UserIdleFor = null)
{
    public static ResourceSample ProcessOnly() => new(DateTimeOffset.UtcNow, ProcessTime());

    public static ResourceSample Machine() => new(DateTimeOffset.UtcNow, ProcessTime(),
        SystemActivityReader.ReadCpuTimes(), SystemActivityReader.ReadUserIdle());

    private static TimeSpan ProcessTime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime;
        }
        catch (Exception)
        {
            // Not available on all platforms. The poll loop must never throw: a faulted
            // BackgroundService stops the host.
            return TimeSpan.Zero;
        }
    }
}
