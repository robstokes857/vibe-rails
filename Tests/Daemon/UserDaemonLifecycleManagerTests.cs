using VibeRails.Daemon;
using VibeRails.Daemon.Ipc;
using VibeRails.Daemon.Platform;
using Xunit;

namespace Tests.Daemon;

public sealed class UserDaemonLifecycleManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"vbd-lifecycle-{Guid.NewGuid():N}");

    [Fact]
    public async Task Stop_DoesNotSucceedUntilTheDaemonInstanceGuardIsReleased()
    {
        Directory.CreateDirectory(_root);
        var identity = new CurrentUserIdentity(
            "test-user",
            _root,
            windowsSid: "S-1-5-21-1000",
            unixUserId: 1000);
        var registration = new DaemonRegistration(
            "vbd-test",
            "VBD test",
            Path.Combine(_root, "vb.exe"),
            ["--job-daemon"],
            _root,
            _root);
        var scoped = registration.ForCurrentUser(identity);
        var taskXml = DaemonServiceDefinitionRenderer.RenderWindowsTaskXml(scoped);
        var manager = new UserDaemonLifecycleManager(
            registration,
            new FixedIdentityProvider(identity),
            new WindowsProcessRunner(taskXml),
            new UnreachableControlClient(),
            new WindowsPlatformProvider())
        {
            TransitionTimeout = TimeSpan.FromMilliseconds(20)
        };

        using var held = DaemonInstanceGuard.TryAcquire(
            registration.ApplicationId,
            identity,
            registration.DataDirectory);
        Assert.NotNull(held);

        var blocked = await manager.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(blocked.Success);
        Assert.Contains("instance guard", blocked.Error, StringComparison.OrdinalIgnoreCase);

        held.Dispose();
        var stopped = await manager.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(stopped.Success);
        Assert.Equal(DaemonLifecycleState.InstalledStopped, stopped.Status.State);
    }

    [Fact]
    public async Task Stop_SucceedsForARunningButUnregisteredOrphanDaemon()
    {
        // The NeedsRepair orphan state: the OS registration was deleted while a daemon process
        // still ran. Stop must report success once the process is gone — requiring IsInstalled
        // here made installers abort updates in exactly the state an update should recover.
        Directory.CreateDirectory(_root);
        var identity = new CurrentUserIdentity(
            "test-user",
            _root,
            windowsSid: "S-1-5-21-1000",
            unixUserId: 1000);
        var registration = new DaemonRegistration(
            "vbd-test",
            "VBD test",
            Path.Combine(_root, "vb.exe"),
            ["--job-daemon"],
            _root,
            _root);
        var manager = new UserDaemonLifecycleManager(
            registration,
            new FixedIdentityProvider(identity),
            new NotInstalledProcessRunner(),
            new UnreachableControlClient(),
            new WindowsPlatformProvider())
        {
            TransitionTimeout = TimeSpan.FromMilliseconds(20)
        };

        var stopped = await manager.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(stopped.Success);
        Assert.Equal(DaemonLifecycleState.NotInstalled, stopped.Status.State);
        Assert.False(stopped.Status.IsReachable);
    }

    [Fact]
    public async Task Repair_DoesNotRewriteOrStartWhileAWedgedManualProcessHoldsTheGuard()
    {
        Directory.CreateDirectory(_root);
        var identity = new CurrentUserIdentity(
            "test-user",
            _root,
            windowsSid: "S-1-5-21-1000",
            unixUserId: 1000);
        var registration = new DaemonRegistration(
            "vbd-test",
            "VBD test",
            Path.Combine(_root, "vb.exe"),
            ["--job-daemon"],
            _root,
            _root);
        var scoped = registration.ForCurrentUser(identity);
        var taskXml = DaemonServiceDefinitionRenderer.RenderWindowsTaskXml(scoped);
        var runner = new WindowsProcessRunner(taskXml);
        var manager = new UserDaemonLifecycleManager(
            registration,
            new FixedIdentityProvider(identity),
            runner,
            new UnreachableControlClient(),
            new WindowsPlatformProvider())
        {
            TransitionTimeout = TimeSpan.FromMilliseconds(20)
        };

        using var held = DaemonInstanceGuard.TryAcquire(
            registration.ApplicationId,
            identity,
            registration.DataDirectory);
        Assert.NotNull(held);

        var result = await manager.RepairAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("instance guard", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(runner.Requests, request => request.Arguments[0] == "/End");
        Assert.DoesNotContain(runner.Requests, request => request.Arguments[0] == "/Create");
        Assert.DoesNotContain(runner.Requests, request => request.Arguments[0] == "/Run");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A failed cleanup should not hide the lifecycle assertion.
        }
    }

    private sealed class FixedIdentityProvider(CurrentUserIdentity identity) : ICurrentUserIdentityProvider
    {
        public CurrentUserIdentity GetCurrent() => identity;
    }

    private sealed class WindowsPlatformProvider : IDaemonPlatformProvider
    {
        public DaemonPlatformKind Current => DaemonPlatformKind.Windows;
    }

    private sealed class WindowsProcessRunner(string taskXml) : IDaemonProcessRunner
    {
        public List<DaemonProcessRequest> Requests { get; } = [];

        public Task<DaemonProcessResult> RunAsync(
            DaemonProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var query = request.Arguments.Any(argument =>
                argument.Equals("/Query", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new DaemonProcessResult(
                0,
                query ? taskXml : string.Empty,
                string.Empty));
        }
    }

    private sealed class NotInstalledProcessRunner : IDaemonProcessRunner
    {
        public Task<DaemonProcessResult> RunAsync(
            DaemonProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            var query = request.Arguments.Any(argument =>
                argument.Equals("/Query", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(query
                ? new DaemonProcessResult(1, string.Empty, "ERROR: The system cannot find the file specified.")
                : new DaemonProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class UnreachableControlClient : IDaemonControlClient
    {
        public Task<DaemonControlClientResult> SendAsync(
            string pipeName,
            DaemonControlCommand command,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DaemonControlClientResult(DaemonControlClientOutcome.Unreachable));

        public Task<DaemonControlClientResult> SendAsync(
            string pipeName,
            DaemonControlRequest request,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DaemonControlClientResult(DaemonControlClientOutcome.Unreachable));
    }
}
