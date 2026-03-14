using System.Diagnostics;

namespace VibeRails.Services;

public sealed record ResourceSnapshot
{
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>Our process CPU usage normalized across all cores (0–100).</summary>
    public double ProcessCpuPercent { get; init; }

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
    /// True when process CPU &gt; 70% or system memory exceeds the GC's
    /// built-in high-memory threshold.
    /// </summary>
    bool IsUnderPressure { get; }
}

public sealed class SystemResourceService : BackgroundService, ISystemResourceService
{
    private const double CpuPressureThreshold = 70.0;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private ResourceSnapshot _current = null!;
    private CpuSample _lastSample;

    public ResourceSnapshot Startup { get; }
    public ResourceSnapshot Current => Volatile.Read(ref _current);

    public bool IsUnderPressure
    {
        get
        {
            var gcInfo = GC.GetGCMemoryInfo();
            return Current.ProcessCpuPercent > CpuPressureThreshold
                || gcInfo.MemoryLoadBytes >= gcInfo.HighMemoryLoadThresholdBytes;
        }
    }

    public SystemResourceService()
    {
        _lastSample = TakeCpuSample();
        // CPU starts at 0 — first real reading arrives after one PollInterval tick.
        var snap = BuildSnapshot(procCpu: 0);
        Startup = snap;
        _current = snap;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var sample = TakeCpuSample();
                var cpu = ComputeProcessCpu(_lastSample, sample);
                _lastSample = sample;
                Volatile.Write(ref _current, BuildSnapshot(cpu));
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static ResourceSnapshot BuildSnapshot(double procCpu)
    {
        var gcInfo = GC.GetGCMemoryInfo();
        return new ResourceSnapshot
        {
            CapturedAt         = DateTimeOffset.UtcNow,
            ProcessCpuPercent  = Math.Round(procCpu, 1),
            TotalMemoryBytes   = gcInfo.TotalAvailableMemoryBytes,
            UsedMemoryBytes    = gcInfo.MemoryLoadBytes,
            ProcessMemoryBytes = Environment.WorkingSet,
        };
    }

    private static CpuSample TakeCpuSample()
    {
        TimeSpan procTime = TimeSpan.Zero;
        try { procTime = Process.GetCurrentProcess().TotalProcessorTime; }
        catch { /* not available on all platforms */ }
        return new CpuSample(procTime, DateTimeOffset.UtcNow);
    }

    private static double ComputeProcessCpu(CpuSample prev, CpuSample curr)
    {
        var wallTicks = (curr.At - prev.At).Ticks;
        if (wallTicks <= 0) return 0;
        var cpuTicks = (curr.ProcessorTime - prev.ProcessorTime).Ticks;
        return Math.Clamp(
            (double)cpuTicks / (wallTicks * Environment.ProcessorCount) * 100,
            0, 100);
    }

    private readonly record struct CpuSample(TimeSpan ProcessorTime, DateTimeOffset At);
}
