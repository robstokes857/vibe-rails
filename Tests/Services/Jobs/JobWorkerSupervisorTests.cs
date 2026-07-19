using VibeRails.DTOs;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobWorkerSupervisorTests
{
    private static readonly DateTime Now = new(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildStatus_RequiresRegistrationFreshHeartbeatAndCurrentVersion()
    {
        var lease = Lease("1.2.3", Now.AddSeconds(-5));

        var status = JobWorkerSupervisor.BuildStatus(
            installed: true,
            lease,
            Now,
            currentVersion: "1.2.3");

        Assert.Equal("healthy", status.State);
        Assert.True(status.Running);
        Assert.False(status.NeedsRepair);
    }

    [Fact]
    public void BuildStatus_FlagsOutdatedWorkerEvenWithFreshHeartbeat()
    {
        var status = JobWorkerSupervisor.BuildStatus(
            installed: true,
            Lease("1.0.0", Now.AddSeconds(-5)),
            Now,
            currentVersion: "2.0.0");

        Assert.Equal("outdated", status.State);
        Assert.True(status.Running);
        Assert.True(status.NeedsRepair);
        Assert.Contains("2.0.0", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatus_FlagsStaleOrUnregisteredWorker()
    {
        var stale = JobWorkerSupervisor.BuildStatus(
            installed: true,
            Lease("2.0.0", Now.AddMinutes(-1)),
            Now,
            currentVersion: "2.0.0");
        var unregistered = JobWorkerSupervisor.BuildStatus(
            installed: false,
            Lease("2.0.0", Now.AddSeconds(-5)),
            Now,
            currentVersion: "2.0.0");

        Assert.Equal("stopped", stale.State);
        Assert.False(stale.Running);
        Assert.True(stale.NeedsRepair);
        Assert.Equal("unregistered", unregistered.State);
        Assert.True(unregistered.Running);
        Assert.True(unregistered.NeedsRepair);
    }

    private static JobWorkerLeaseRecord Lease(string version, DateTime heartbeatUtc) => new(
        "worker",
        42,
        version,
        Now.AddMinutes(-5),
        heartbeatUtc);
}
