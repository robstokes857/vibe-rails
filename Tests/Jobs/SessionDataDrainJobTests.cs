using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Jobs;
using VibeRails.Services;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;
using Xunit;

namespace Tests.Jobs;

public sealed class SessionDataDrainJobTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 3, 18, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExecuteJob_OptOut_SweepsSpoolButTouchesNothingElse()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var exportService = new Mock<ISessionDataExportService>(MockBehavior.Strict);
        SetupSweep(exportService);
        using var services = BuildServices(repository.Object);
        var job = CreateJob(services, exportService.Object, dataExportOptIn: false);

        await InvokeExecuteJobAsync(job, TestContext.Current.CancellationToken);

        // The sweep is deliberately above the consent gate: a user who opted out with a spool
        // still on disk is exactly the one whose leftover session material must be reclaimed.
        exportService.Verify(
            service => service.SweepOrphanedSpoolAsync(It.IsAny<CancellationToken>()),
            Times.Once);
        repository.VerifyNoOtherCalls();
        exportService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteJob_UnconfiguredTransport_SweepsSpoolButDoesNotQueryDatabase()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var exportService = new Mock<ISessionDataExportService>(MockBehavior.Strict);
        SetupSweep(exportService);
        exportService.SetupGet(service => service.IsConfigured).Returns(false);
        using var services = BuildServices(repository.Object);
        var job = CreateJob(services, exportService.Object, dataExportOptIn: true);

        await InvokeExecuteJobAsync(job, TestContext.Current.CancellationToken);

        repository.VerifyNoOtherCalls();
        exportService.VerifyGet(service => service.IsConfigured, Times.Once);
        exportService.Verify(
            service => service.SweepOrphanedSpoolAsync(It.IsAny<CancellationToken>()),
            Times.Once);
        exportService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteJob_NoEligibleSession_DoesNotCallTransport()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        SetupSelection(repository, null);
        var exportService = ConfiguredExportService();
        using var services = BuildServices(repository.Object);
        var job = CreateJob(services, exportService.Object, dataExportOptIn: true);

        await InvokeExecuteJobAsync(job, TestContext.Current.CancellationToken);

        repository.VerifyAll();
        exportService.Verify(service => service.ExportSessionAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteJob_ExportsExactlyTheOldestSettledSession()
    {
        const string sessionId = "session-oldest";
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        SetupSelection(repository, new UnexportedSessionRef(sessionId, 0));
        var exportService = ConfiguredExportService();
        exportService
            .Setup(service => service.ExportSessionAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionDataExportResult(
                SessionDataExportStatus.Success,
                sessionId,
                Sha256: new string('a', 64)));
        using var services = BuildServices(repository.Object);
        var job = CreateJob(services, exportService.Object, dataExportOptIn: true);

        await InvokeExecuteJobAsync(job, TestContext.Current.CancellationToken);

        repository.VerifyAll();
        // A success must not write a deferral: ExportedUTC already took it out of the queue.
        repository.Verify(
            repo => repo.DeferSessionExportAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
        exportService.Verify(service => service.ExportSessionAsync(
            sessionId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    // attempts recorded before this failure -> the backoff written after it
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(3, 16)]
    public async Task ExecuteJob_FailedExport_DefersInsteadOfHoldingTheQueueHead(
        int priorAttempts,
        int expectedBackoffMinutes)
    {
        const string sessionId = "session-retry";
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        SetupSelection(repository, new UnexportedSessionRef(sessionId, priorAttempts));
        repository
            .Setup(repo => repo.DeferSessionExportAsync(
                sessionId,
                Now.UtcDateTime + TimeSpan.FromMinutes(expectedBackoffMinutes),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var exportService = ConfiguredExportService();
        exportService
            .Setup(service => service.ExportSessionAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionDataExportResult(
                SessionDataExportStatus.UploadFailed,
                sessionId,
                Detail: "HTTP 503."));
        using var services = BuildServices(repository.Object);
        var job = CreateJob(services, exportService.Object, dataExportOptIn: true);

        await InvokeExecuteJobAsync(job, TestContext.Current.CancellationToken);

        // Deferred, never abandoned: the session keeps its place and is retried after the backoff.
        repository.VerifyAll();
        repository.Verify(
            repo => repo.MarkSessionExportedAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteJob_SlowFailure_MeasuresTheBackoffFromWhenTheAttemptEnded()
    {
        const string sessionId = "session-hung";
        // A hung upload gives up after PayloadTimeout, which is exactly MinRetryBackoff. A deadline
        // anchored on the tick's opening clock reading would therefore be written already expired,
        // and the session that just failed would win the queue head back on the very next tick.
        var attemptDuration = TimeSpan.FromMinutes(2);
        var clock = new FixedTimeProvider(Now);

        DateTime? deferredUntil = null;
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        SetupSelection(repository, new UnexportedSessionRef(sessionId, 0));
        repository
            .Setup(repo => repo.DeferSessionExportAsync(
                sessionId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<string, DateTime, CancellationToken>((_, until, _) => deferredUntil = until)
            .ReturnsAsync(true);

        var exportService = ConfiguredExportService();
        exportService
            .Setup(service => service.ExportSessionAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                clock.UtcNow = clock.UtcNow.Add(attemptDuration);
                return new SessionDataExportResult(
                    SessionDataExportStatus.UploadFailed, sessionId, Detail: "The upload timed out.");
            });

        using var services = BuildServices(repository.Object);
        var job = CreateJob(services, exportService.Object, dataExportOptIn: true, clock);

        await InvokeExecuteJobAsync(job, TestContext.Current.CancellationToken);

        Assert.NotNull(deferredUntil);
        // The invariant that matters: the deferral is in the future as of the moment it is written.
        Assert.True(
            deferredUntil > clock.UtcNow.UtcDateTime,
            $"Deferred until {deferredUntil:O}, which is not after the post-attempt clock {clock.UtcNow.UtcDateTime:O}.");
        Assert.Equal(clock.UtcNow.UtcDateTime + SessionDataDrainJob.MinRetryBackoff, deferredUntil);
    }

    [Fact]
    public async Task ExecuteJob_BusyExport_IsNotChargedAsAFailedAttempt()
    {
        const string sessionId = "session-busy";
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        SetupSelection(repository, new UnexportedSessionRef(sessionId, 0));
        var exportService = ConfiguredExportService();
        exportService
            .Setup(service => service.ExportSessionAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionDataExportResult(
                SessionDataExportStatus.Busy,
                sessionId,
                Detail: "Another data export is running."));
        using var services = BuildServices(repository.Object);
        var job = CreateJob(services, exportService.Object, dataExportOptIn: true);

        await InvokeExecuteJobAsync(job, TestContext.Current.CancellationToken);

        // Busy means another export (or another root backend) holds the gate, not that this
        // session is bad. Backing it off would penalise a healthy session for someone else's lock.
        repository.Verify(
            repo => repo.DeferSessionExportAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteJob_CancelledBeforeTick_DoesNoWork()
    {
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var exportService = new Mock<ISessionDataExportService>(MockBehavior.Strict);
        using var services = BuildServices(repository.Object);
        var job = CreateJob(services, exportService.Object, dataExportOptIn: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeExecuteJobAsync(job, cancellation.Token));

        repository.VerifyNoOtherCalls();
        exportService.VerifyNoOtherCalls();
    }

    [Fact]
    public void BackoffFor_GrowsFromTheMinimumAndClampsToTheMaximum()
    {
        Assert.Equal(SessionDataDrainJob.MinRetryBackoff, SessionDataDrainJob.BackoffFor(0));
        Assert.Equal(SessionDataDrainJob.MinRetryBackoff, SessionDataDrainJob.BackoffFor(1));
        Assert.Equal(TimeSpan.FromMinutes(4), SessionDataDrainJob.BackoffFor(2));
        Assert.Equal(TimeSpan.FromMinutes(8), SessionDataDrainJob.BackoffFor(3));

        // Clamped rather than overflowing, and never infinite: deferred, not dropped.
        Assert.Equal(SessionDataDrainJob.MaxRetryBackoff, SessionDataDrainJob.BackoffFor(100));
        Assert.Equal(SessionDataDrainJob.MaxRetryBackoff, SessionDataDrainJob.BackoffFor(int.MaxValue));
    }

    private static void SetupSelection(Mock<IRepository> repository, UnexportedSessionRef? selected) =>
        repository
            .Setup(repo => repo.GetOldestUnexportedSessionAsync(
                Now.UtcDateTime - SessionDataDrainJob.SessionSettleDelay,
                Now.UtcDateTime,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(selected);

    private static void SetupSweep(Mock<ISessionDataExportService> service) =>
        service
            .Setup(candidate => candidate.SweepOrphanedSpoolAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

    private static Mock<ISessionDataExportService> ConfiguredExportService()
    {
        var service = new Mock<ISessionDataExportService>(MockBehavior.Strict);
        service.SetupGet(candidate => candidate.IsConfigured).Returns(true);
        SetupSweep(service);
        return service;
    }

    private static ServiceProvider BuildServices(IRepository repository) =>
        new ServiceCollection()
            .AddSingleton(repository)
            .BuildServiceProvider();

    private static SessionDataDrainJob CreateJob(
        ServiceProvider services,
        ISessionDataExportService exportService,
        bool dataExportOptIn,
        FixedTimeProvider? clock = null) =>
        new(
            NullLogger<SessionDataDrainJob>.Instance,
            Mock.Of<ISystemResourceService>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            exportService,
            () => new Settings { DataExportOptIn = dataExportOptIn },
            clock ?? new FixedTimeProvider(Now));

    private static async Task InvokeExecuteJobAsync(
        SessionDataDrainJob job,
        CancellationToken cancellationToken = default)
    {
        var method = typeof(SessionDataDrainJob).GetMethod(
            "ExecuteJob",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SessionDataDrainJob.ExecuteJob was not found.");
        var task = (Task?)method.Invoke(job, [cancellationToken])
            ?? throw new InvalidOperationException("SessionDataDrainJob.ExecuteJob returned null.");
        await task;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
