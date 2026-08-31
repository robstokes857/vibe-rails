using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Moq;
using VibeRails;
using VibeRails.Daemon;
using VibeRails.Daemon.Ipc;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobDaemonControlHandlerTests
{
    [Fact]
    public async Task Kicker_SendsABoundedCurrentUserKick()
    {
        var profileDirectory = Path.GetFullPath(Path.GetTempPath());
        var identity = new CurrentUserIdentity("test-user", profileDirectory);
        var identityProvider = new Mock<ICurrentUserIdentityProvider>(MockBehavior.Strict);
        identityProvider.Setup(candidate => candidate.GetCurrent()).Returns(identity);
        var expectedPipeName = $"{JobDaemonRegistrationFactory.ApplicationId}-{identity.ScopeKey}-ipc";
        var controlClient = new Mock<IDaemonControlClient>(MockBehavior.Strict);
        controlClient
            .Setup(candidate => candidate.SendAsync(
                expectedPipeName,
                DaemonControlCommand.Kick,
                TimeSpan.FromMilliseconds(350),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DaemonControlClientResult(DaemonControlClientOutcome.Success));
        var kicker = new JobDaemonKicker(
            controlClient.Object,
            identityProvider.Object,
            daemonMayBeRunning: _ => true);

        await kicker.KickAsync(TestContext.Current.CancellationToken);

        controlClient.VerifyAll();
        identityProvider.Verify(candidate => candidate.GetCurrent(), Times.Once);
    }

    [Fact]
    public async Task Kicker_SkipsThePipeEntirelyWhenNoDaemonProcessIsPresent()
    {
        // The 350ms connect timeout is user-visible latency on Run-now/retry/commit paths.
        // When the instance-guard probe says no VBD process exists, no pipe attempt happens.
        var profileDirectory = Path.GetFullPath(Path.GetTempPath());
        var identity = new CurrentUserIdentity("test-user", profileDirectory);
        var identityProvider = new Mock<ICurrentUserIdentityProvider>(MockBehavior.Strict);
        identityProvider.Setup(candidate => candidate.GetCurrent()).Returns(identity);
        var controlClient = new Mock<IDaemonControlClient>(MockBehavior.Strict);
        var kicker = new JobDaemonKicker(
            controlClient.Object,
            identityProvider.Object,
            daemonMayBeRunning: _ => false);

        await kicker.KickAsync(TestContext.Current.CancellationToken);

        controlClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Status_ReportsContendedCyclesAsLiveCycles()
    {
        // A VBD contended out of the durable lease by an open dashboard still cycles healthily.
        // The status payload's "last cycle" is a liveness signal and must advance on contended
        // cycles too — mapping only successful owner cycles left it "Not reported" forever.
        var scheduler = new Mock<IJobScheduler>(MockBehavior.Strict);
        var lifetime = new Mock<IHostApplicationLifetime>(MockBehavior.Strict);
        var health = new JobSchedulerHealth();
        var contendedUtc = new DateTime(2026, 8, 30, 16, 0, 0, DateTimeKind.Utc);
        health.CycleContended(contendedUtc);
        var handler = new JobDaemonControlHandler(
            scheduler.Object,
            health,
            new JobDaemonRuntimeInfo(DateTime.UtcNow.AddSeconds(-5)),
            lifetime.Object);

        var result = await handler.HandleAsync(
            DaemonControlCommand.Status,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var payload = JsonSerializer.Deserialize(
            result.Payload!.Value.GetRawText(),
            JobDaemonControlJsonContext.Default.JobDaemonControlStatusPayload);
        Assert.NotNull(payload);
        Assert.Equal(contendedUtc, payload.LastCycleUtc);
        Assert.False(payload.OwnsSchedulerLease);
        Assert.Null(payload.LastError);
    }

    [Fact]
    public async Task Status_ReturnsVersionProcessAndSchedulerHealth()
    {
        var scheduler = new Mock<IJobScheduler>(MockBehavior.Strict);
        var lifetime = new Mock<IHostApplicationLifetime>(MockBehavior.Strict);
        var health = new JobSchedulerHealth();
        var lastCycleUtc = new DateTime(2026, 8, 30, 15, 45, 0, DateTimeKind.Utc);
        health.CycleCompleted(
            lastCycleUtc,
            ownsLease: true,
            schedulesEnqueued: 2,
            runsLaunched: 1,
            runsReaped: 0,
            stalledLaunchesFailed: 0);
        var startedUtc = DateTime.UtcNow.AddSeconds(-10);
        var handler = new JobDaemonControlHandler(
            scheduler.Object,
            health,
            new JobDaemonRuntimeInfo(startedUtc),
            lifetime.Object);

        var result = await handler.HandleAsync(
            DaemonControlCommand.Status,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        var payload = JsonSerializer.Deserialize(
            result.Payload.Value.GetRawText(),
            JobDaemonControlJsonContext.Default.JobDaemonControlStatusPayload);
        Assert.NotNull(payload);
        Assert.Equal(VersionInfo.Version, payload.Version);
        Assert.Equal(DaemonControlProtocol.Version, payload.ProtocolVersion);
        Assert.Equal(Environment.ProcessId, payload.Pid);
        Assert.Equal(startedUtc, payload.StartedUtc);
        Assert.True(payload.UptimeSeconds >= 0);
        Assert.Equal(lastCycleUtc, payload.LastCycleUtc);
        Assert.True(payload.OwnsSchedulerLease);
        Assert.Null(payload.LastError);
        Assert.Null(result.AfterResponse);
        scheduler.VerifyNoOtherCalls();
        lifetime.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Kick_WakesTheSchedulerImmediately()
    {
        var scheduler = new Mock<IJobScheduler>(MockBehavior.Strict);
        scheduler.Setup(candidate => candidate.Kick());
        var lifetime = new Mock<IHostApplicationLifetime>(MockBehavior.Strict);
        var handler = CreateHandler(scheduler.Object, lifetime.Object);

        var result = await handler.HandleAsync(
            DaemonControlCommand.Kick,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("queued", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.AfterResponse);
        scheduler.Verify(candidate => candidate.Kick(), Times.Once);
        lifetime.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Shutdown_StopsTheHostOnlyAfterTheResponseCallbackRuns()
    {
        var scheduler = new Mock<IJobScheduler>(MockBehavior.Strict);
        var stopCalls = 0;
        var lifetime = new Mock<IHostApplicationLifetime>(MockBehavior.Strict);
        lifetime
            .Setup(candidate => candidate.StopApplication())
            .Callback(() => Interlocked.Increment(ref stopCalls));
        var handler = CreateHandler(scheduler.Object, lifetime.Object);

        var result = await handler.HandleAsync(
            DaemonControlCommand.Shutdown,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(0, Volatile.Read(ref stopCalls));
        var afterResponse = Assert.IsType<Func<ValueTask>>(result.AfterResponse);

        await afterResponse();

        Assert.Equal(1, Volatile.Read(ref stopCalls));
        lifetime.Verify(candidate => candidate.StopApplication(), Times.Once);
        scheduler.VerifyNoOtherCalls();
    }

    private static JobDaemonControlHandler CreateHandler(
        IJobScheduler scheduler,
        IHostApplicationLifetime lifetime) => new(
        scheduler,
        new JobSchedulerHealth(),
        new JobDaemonRuntimeInfo(DateTime.UtcNow),
        lifetime);
}
