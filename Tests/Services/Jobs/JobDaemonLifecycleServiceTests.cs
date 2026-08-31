using Moq;
using VibeRails;
using VibeRails.Daemon;
using VibeRails.Daemon.Ipc;
using VibeRails.DTOs;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobDaemonLifecycleServiceTests
{
    [Fact]
    public async Task UnavailableRegistration_ReturnsUnavailableWithoutContactingTheOs()
    {
        var installDirectory = Path.Combine(Path.GetTempPath(), "missing-vbd-install");
        var resolution = JobDaemonRegistrationResolution.Unavailable(
            installDirectory,
            "Stable installation unavailable.");
        var controlClient = UnreachableControlClient();
        var identityProvider = IdentityProvider(installDirectory);
        var service = new JobDaemonLifecycleService(
            new JobDaemonRegistrationProvider(() => resolution),
            controlClient.Object,
            identityProvider.Object);

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JobDaemonState.Unavailable, status.State);
        Assert.False(status.IsSupported);
        Assert.False(status.IsInstalled);
        Assert.False(status.IsRunning);
        Assert.False(status.IsReachable);
        Assert.False(status.RegistrationIsCurrent);
        Assert.Equal(VersionInfo.Version, status.CurrentVersion);
        Assert.Equal("Stable installation unavailable.", status.LastError);
        Assert.Empty(status.AllowedActions!);
        // Only the liveness ping is allowed: no Status/Kick command and no OS lifecycle work.
        controlClient.Verify(
            candidate => candidate.SendAsync(
                It.IsAny<string>(),
                DaemonControlCommand.Ping,
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        controlClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnavailableRegistration_StillReportsAReachableDaemonAsRunning()
    {
        // Installers decide whether an update may proceed from this status. A daemon that is
        // still reachable must surface as running even when the registration cannot resolve
        // (custom install dir, failed path validation), or updates would replace files under it.
        var installDirectory = Path.Combine(Path.GetTempPath(), "missing-vbd-install");
        var resolution = JobDaemonRegistrationResolution.Unavailable(
            installDirectory,
            "Stable installation unavailable.");
        var controlClient = new Mock<IDaemonControlClient>(MockBehavior.Strict);
        controlClient
            .Setup(candidate => candidate.SendAsync(
                It.IsAny<string>(),
                DaemonControlCommand.Ping,
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DaemonControlClientResult(
                DaemonControlClientOutcome.Success,
                new DaemonControlResponse(DaemonControlProtocol.Version, true)));
        var identityProvider = IdentityProvider(installDirectory);
        var service = new JobDaemonLifecycleService(
            new JobDaemonRegistrationProvider(() => resolution),
            controlClient.Object,
            identityProvider.Object);

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JobDaemonState.Unavailable, status.State);
        Assert.True(status.IsRunning);
        Assert.True(status.IsReachable);
        Assert.Empty(status.AllowedActions!);
    }

    [Fact]
    public async Task Install_WhenRegistrationIsUnavailable_ReturnsAFailedActionWithoutLifecycleMutation()
    {
        var installDirectory = Path.Combine(Path.GetTempPath(), "missing-vbd-install");
        var resolution = JobDaemonRegistrationResolution.Unavailable(
            installDirectory,
            "Install the stable VibeRails payload first.");
        var controlClient = UnreachableControlClient();
        var identityProvider = IdentityProvider(installDirectory);
        var service = new JobDaemonLifecycleService(
            new JobDaemonRegistrationProvider(() => resolution),
            controlClient.Object,
            identityProvider.Object);

        var action = await service.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(action.Success);
        Assert.Contains("stable VibeRails payload", action.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JobDaemonState.Unavailable, action.Status.State);
        Assert.Empty(action.Status.AllowedActions!);
        controlClient.Verify(
            candidate => candidate.SendAsync(
                It.IsAny<string>(),
                DaemonControlCommand.Ping,
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        controlClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Status_ReResolvesTheRegistrationOnEveryCall()
    {
        // A one-time resolution would latch "vb.exe is missing" for the process lifetime; the
        // provider must let a later successful resolution replace an unusable one.
        var installDirectory = Path.Combine(Path.GetTempPath(), "missing-vbd-install");
        var resolutions = 0;
        var provider = new JobDaemonRegistrationProvider(() =>
        {
            resolutions++;
            return JobDaemonRegistrationResolution.Unavailable(
                installDirectory,
                $"Attempt {resolutions}.");
        });
        var controlClient = UnreachableControlClient();
        var service = new JobDaemonLifecycleService(
            provider,
            controlClient.Object,
            IdentityProvider(installDirectory).Object);

        var first = await service.GetStatusAsync(TestContext.Current.CancellationToken);
        var second = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Attempt 1.", first.LastError);
        Assert.Equal("Attempt 2.", second.LastError);
        Assert.True(resolutions >= 2);
    }

    private static Mock<IDaemonControlClient> UnreachableControlClient()
    {
        var controlClient = new Mock<IDaemonControlClient>(MockBehavior.Strict);
        controlClient
            .Setup(candidate => candidate.SendAsync(
                It.IsAny<string>(),
                It.IsAny<DaemonControlCommand>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DaemonControlClientResult(DaemonControlClientOutcome.Unreachable));
        return controlClient;
    }

    private static Mock<ICurrentUserIdentityProvider> IdentityProvider(string profileDirectory)
    {
        var provider = new Mock<ICurrentUserIdentityProvider>(MockBehavior.Strict);
        provider
            .Setup(candidate => candidate.GetCurrent())
            .Returns(new CurrentUserIdentity("test-user", profileDirectory));
        return provider;
    }
}
