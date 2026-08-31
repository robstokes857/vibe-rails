using System.Collections.Concurrent;
using VibeRails.Daemon;
using VibeRails.Daemon.Ipc;
using VibeRails.Daemon.Platform;
using Xunit;

namespace Tests.Daemon;

public sealed class WindowsTaskSchedulerLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"vbd-windows-lifecycle-{Guid.NewGuid():N}");

    [Fact]
    public async Task Stop_GivesSuccessfulIpcShutdownTimeToReleaseGuardBeforeForcedEnd()
    {
        var (registration, identity, scoped) = CreateRegistration();
        var runner = new RecordingProcessRunner();
        DaemonInstanceGuard? held = DaemonInstanceGuard.TryAcquire(
            registration.ApplicationId,
            identity,
            registration.DataDirectory);
        Assert.NotNull(held);

        var releaseCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DelegateControlClient(command =>
        {
            Assert.Equal(DaemonControlCommand.Shutdown, command);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
                held.Dispose();
                releaseCompleted.TrySetResult();
            });
            return new DaemonControlClientResult(DaemonControlClientOutcome.Success);
        });
        var lifecycle = new WindowsTaskSchedulerLifecycle(scoped, runner, client)
        {
            GracefulShutdownTimeout = TimeSpan.FromSeconds(1)
        };

        try
        {
            await lifecycle.StopAsync(TestContext.Current.CancellationToken);
            await releaseCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Empty(runner.Requests);
        }
        finally
        {
            held.Dispose();
        }
    }

    [Fact]
    public async Task Stop_UsesForcedEndAfterGraceWindowExpiresWithGuardHeld()
    {
        var (registration, identity, scoped) = CreateRegistration();
        var runner = new RecordingProcessRunner();
        var client = new DelegateControlClient(_ =>
            new DaemonControlClientResult(DaemonControlClientOutcome.Success));
        var lifecycle = new WindowsTaskSchedulerLifecycle(scoped, runner, client)
        {
            GracefulShutdownTimeout = TimeSpan.FromMilliseconds(20)
        };
        using var held = DaemonInstanceGuard.TryAcquire(
            registration.ApplicationId,
            identity,
            registration.DataDirectory);
        Assert.NotNull(held);

        await lifecycle.StopAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(runner.Requests);
        Assert.Equal("/End", request.Arguments[0]);
        Assert.Equal(scoped.WindowsTaskName, request.Arguments[2]);
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

    private (DaemonRegistration Registration, CurrentUserIdentity Identity, ScopedDaemonRegistration Scoped)
        CreateRegistration()
    {
        Directory.CreateDirectory(_root);
        var identity = new CurrentUserIdentity(
            $"test-user:{Guid.NewGuid():N}",
            _root,
            windowsSid: $"S-1-5-21-{Random.Shared.Next(1000, int.MaxValue)}");
        var registration = new DaemonRegistration(
            "vbd-windows-test",
            "VBD Windows test",
            Path.Combine(_root, "vb.exe"),
            ["--job-daemon"],
            _root,
            _root);
        return (registration, identity, registration.ForCurrentUser(identity));
    }

    private sealed class RecordingProcessRunner : IDaemonProcessRunner
    {
        public ConcurrentQueue<DaemonProcessRequest> Requests { get; } = new();

        public Task<DaemonProcessResult> RunAsync(
            DaemonProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            return Task.FromResult(new DaemonProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class DelegateControlClient(
        Func<DaemonControlCommand, DaemonControlClientResult> send) : IDaemonControlClient
    {
        public Task<DaemonControlClientResult> SendAsync(
            string pipeName,
            DaemonControlCommand command,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(send(command));

        public Task<DaemonControlClientResult> SendAsync(
            string pipeName,
            DaemonControlRequest request,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
