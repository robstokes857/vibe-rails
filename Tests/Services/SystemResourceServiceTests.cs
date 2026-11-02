using VibeRails.Services;
using Xunit;

namespace Tests.Services;

/// <summary>Machine-wide CPU, the quiet window and the pressure thresholds added for VIBE-172.</summary>
public sealed class SystemResourceServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    [Fact]
    public void ProcStatCountsIdleAndIowaitAsNotBusyAndLeavesGuestTimeOut()
    {
        // user nice system idle iowait irq softirq steal guest guest_nice
        var times = SystemActivityReader.ParseProcStat("cpu  400 100 200 1000 300 10 20 30 500 600");

        Assert.Equal(new SystemCpuTimes(Busy: 760, Total: 2060), times);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cpu0 1 2 3 4 5 6 7 8")]
    [InlineData("cpu 1 2 3")]
    [InlineData("cpu 1 2 x 4 5 6 7 8")]
    [InlineData("intr 1 2 3 4 5 6 7 8")]
    public void AnUnreadableProcStatLineGivesNoFigure(string? line) =>
        Assert.Null(SystemActivityReader.ParseProcStat(line));

    [Fact]
    public void CpuPercentIsTheBusyShareOfTheInterval()
    {
        Assert.Equal(25d, SystemActivityReader.CpuPercent(new SystemCpuTimes(100, 1000), new SystemCpuTimes(350, 2000)));
        Assert.Null(SystemActivityReader.CpuPercent(null, new SystemCpuTimes(350, 2000)));
        Assert.Null(SystemActivityReader.CpuPercent(new SystemCpuTimes(100, 1000), null));
        // No elapsed total, or counters that went backwards, is not a usable interval.
        Assert.Null(SystemActivityReader.CpuPercent(new SystemCpuTimes(100, 1000), new SystemCpuTimes(100, 1000)));
        Assert.Null(SystemActivityReader.CpuPercent(new SystemCpuTimes(500, 1000), new SystemCpuTimes(400, 2000)));
    }

    [Fact]
    public void TheMachineCountersAreReadableOnWindowsAndLinux()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        var first = SystemActivityReader.ReadCpuTimes();
        Assert.NotNull(first);
        Assert.True(first.Value.Total > 0);
        Assert.InRange(first.Value.Busy, 0UL, first.Value.Total);
    }

    [Fact]
    public void TheQuietWindowGrowsWhileTheRestOfTheMachineIsQuietAndResetsWhenItIsBusy()
    {
        var feed = new SampleFeed();
        var service = new SystemResourceService(feed.Next);
        Assert.Null(service.Startup.SystemCpuPercent);
        Assert.Equal(TimeSpan.Zero, service.Startup.QuietFor);

        // The whole first interval was quiet, so the window covers it.
        var quiet = service.Record(feed.Advance(systemPercent: 10));
        Assert.Equal(10d, quiet.SystemCpuPercent);
        Assert.Equal(Poll, quiet.QuietFor);
        Assert.Equal(3 * Poll, Repeat(service, feed, 2, systemPercent: 20).QuietFor);

        var busy = service.Record(feed.Advance(systemPercent: 60));
        Assert.Equal(TimeSpan.Zero, busy.QuietFor);
        Assert.Equal(Poll, service.Record(feed.Advance(systemPercent: 5)).QuietFor);
    }

    [Fact]
    public void ThisProcessesOwnCpuDoesNotCountAgainstTheQuietWindow()
    {
        var feed = new SampleFeed();
        var service = new SystemResourceService(feed.Next);

        // The machine is 60% busy, 50 points of it this process's own background work.
        var snapshot = service.Record(feed.Advance(systemPercent: 60, processPercent: 50));

        Assert.Equal(50, snapshot.ProcessCpuPercent, precision: 1);
        Assert.Equal(10, snapshot.OtherCpuPercent!.Value, precision: 1);
        Assert.Equal(Poll, snapshot.QuietFor);
    }

    [Fact]
    public void WithoutAMachineFigureTheQuietWindowStaysClosedAndPressureFallsBackToProcessCpu()
    {
        var feed = new SampleFeed(machine: false);
        var service = new SystemResourceService(feed.Next);

        var snapshot = service.Record(feed.Advance(systemPercent: 0, processPercent: 75));

        Assert.Null(snapshot.SystemCpuPercent);
        Assert.Null(snapshot.OtherCpuPercent);
        Assert.Equal(TimeSpan.Zero, snapshot.QuietFor);
        Assert.True(SystemResourceService.ExceedsCpuThreshold(snapshot));
        Assert.False(SystemResourceService.ExceedsCpuThreshold(snapshot with { ProcessCpuPercent = 65 }));
    }

    [Fact]
    public void PressureIsMachineWideCpuAboveEightyFivePercent()
    {
        // A build saturating the machine defers jobs even though this process is idle.
        Assert.True(SystemResourceService.ExceedsCpuThreshold(new ResourceSnapshot { SystemCpuPercent = 90, ProcessCpuPercent = 1 }));
        // A merely busy box does not.
        Assert.False(SystemResourceService.ExceedsCpuThreshold(new ResourceSnapshot { SystemCpuPercent = 80, ProcessCpuPercent = 1 }));
        Assert.False(SystemResourceService.ExceedsCpuThreshold(new ResourceSnapshot { SystemCpuPercent = 85, ProcessCpuPercent = 1 }));
    }

    [Fact]
    public void UserIdleIsCarriedFromTheSample()
    {
        var feed = new SampleFeed(userIdle: TimeSpan.FromMinutes(7));
        var service = new SystemResourceService(feed.Next);

        Assert.Equal(TimeSpan.FromMinutes(7), service.Record(feed.Advance(systemPercent: 10)).UserIdleFor);
    }

    private static ResourceSnapshot Repeat(SystemResourceService service, SampleFeed feed, int count, double systemPercent)
    {
        ResourceSnapshot last = service.Current;
        for (var i = 0; i < count; i++) last = service.Record(feed.Advance(systemPercent));
        return last;
    }

    /// <summary>Synthetic counters advancing one poll at a time with chosen machine and process CPU.</summary>
    private sealed class SampleFeed(bool machine = true, TimeSpan? userIdle = null)
    {
        private const ulong TicksPerPoll = 1_000_000;
        private DateTimeOffset _at = Start;
        private TimeSpan _processor = TimeSpan.Zero;
        private SystemCpuTimes _times = new(0, 0);

        public ResourceSample Next() => new(_at, _processor, machine ? _times : null, userIdle);

        public ResourceSample Advance(double systemPercent, double processPercent = 0)
        {
            _at += Poll;
            _processor += Poll * Environment.ProcessorCount * (processPercent / 100);
            _times = new(_times.Busy + (ulong)(TicksPerPoll * systemPercent / 100), _times.Total + TicksPerPoll);
            return Next();
        }
    }
}
