using VibeRails.Daemon.Ipc;

namespace VibeRails.Daemon.Platform;

/// <summary>Cross-platform current-user lifecycle facade with live IPC-aware status mapping.</summary>
public sealed class UserDaemonLifecycleManager : IUserDaemonLifecycleManager
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan LifecycleLockTimeout = TimeSpan.FromSeconds(30);

    private readonly ScopedDaemonRegistration _scoped;
    private readonly IDaemonControlClient _controlClient;
    private readonly IDaemonPlatformLifecycle? _platformLifecycle;
    private readonly DaemonPlatformKind _platform;

    internal TimeSpan TransitionTimeout { get; set; } = TimeSpan.FromSeconds(8);

    public UserDaemonLifecycleManager(
        DaemonRegistration registration,
        ICurrentUserIdentityProvider? identityProvider = null,
        IDaemonProcessRunner? processRunner = null,
        IDaemonControlClient? controlClient = null,
        IDaemonPlatformProvider? platformProvider = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        identityProvider ??= new CurrentUserIdentityProvider();
        processRunner ??= new DaemonProcessRunner();
        controlClient ??= new DaemonControlClient();
        platformProvider ??= new RuntimeDaemonPlatformProvider();

        _scoped = registration.ForCurrentUser(identityProvider.GetCurrent());
        _controlClient = controlClient;
        _platform = platformProvider.Current;
        _platformLifecycle = _platform switch
        {
            DaemonPlatformKind.Windows => new WindowsTaskSchedulerLifecycle(_scoped, processRunner, controlClient),
            DaemonPlatformKind.Linux => new SystemdUserLifecycle(_scoped, processRunner, controlClient),
            DaemonPlatformKind.MacOS => new LaunchAgentLifecycle(_scoped, processRunner, controlClient),
            _ => null
        };
    }

    public async Task<DaemonLifecycleStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_platformLifecycle is null)
        {
            return DaemonLifecycleStatusMapper.Map(
                _platform,
                new DaemonRegistrationInspection(
                    DaemonRegistrationCondition.Unavailable,
                    "Current-user daemons are not supported on this operating system."),
                new DaemonControlClientResult(DaemonControlClientOutcome.Unreachable));
        }

        try
        {
            var inspectionTask = _platformLifecycle.InspectAsync(cancellationToken);
            var reachabilityTask = _controlClient.SendAsync(
                _scoped.PipeName,
                DaemonControlCommand.Ping,
                StatusTimeout,
                cancellationToken);
            await Task.WhenAll(inspectionTask, reachabilityTask).ConfigureAwait(false);
            return DaemonLifecycleStatusMapper.Map(
                _platform,
                await inspectionTask.ConfigureAwait(false),
                await reachabilityTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DaemonLifecycleStatus(
                DaemonLifecycleState.Error,
                _platform,
                IsSupported: true,
                IsInstalled: false,
                IsReachable: false,
                RegistrationIsCurrent: false,
                Error: ex.Message);
        }
    }

    public Task<DaemonLifecycleResult> InstallAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async lifecycle =>
        {
            await lifecycle.CleanupLegacyRegistrationsAsync(cancellationToken).ConfigureAwait(false);
            await lifecycle.InstallAsync(cancellationToken).ConfigureAwait(false);
            await WaitForReachabilityAsync(expected: true, cancellationToken).ConfigureAwait(false);
        }, status => status.State == DaemonLifecycleState.Running,
        "The daemon registration was installed but the process did not become reachable.", cancellationToken);

    public Task<DaemonLifecycleResult> StartAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async lifecycle =>
        {
            await lifecycle.StartAsync(cancellationToken).ConfigureAwait(false);
            await WaitForReachabilityAsync(expected: true, cancellationToken).ConfigureAwait(false);
        }, status => status.State == DaemonLifecycleState.Running,
        "The daemon was started but did not become reachable.", cancellationToken);

    public Task<DaemonLifecycleResult> StopAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async lifecycle =>
        {
            await lifecycle.StopAsync(cancellationToken).ConfigureAwait(false);
            await WaitForReachabilityAsync(expected: false, cancellationToken).ConfigureAwait(false);
        // Deliberately no IsInstalled requirement: stopping a running-but-unregistered orphan
        // (the NeedsRepair state an update must recover from) is a success once it is unreachable
        // and has released the instance guard, which WaitForReachabilityAsync already enforced.
        }, status => !status.IsReachable,
        "The daemon did not stop within the expected time.", cancellationToken);

    public Task<DaemonLifecycleResult> RestartAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async lifecycle =>
        {
            await lifecycle.StopAsync(cancellationToken).ConfigureAwait(false);
            await WaitForReachabilityAsync(expected: false, cancellationToken).ConfigureAwait(false);
            await lifecycle.StartAsync(cancellationToken).ConfigureAwait(false);
            await WaitForReachabilityAsync(expected: true, cancellationToken).ConfigureAwait(false);
        }, status => status.State == DaemonLifecycleState.Running,
        "The daemon did not become reachable after restart.", cancellationToken);

    public async Task<DaemonLifecycleResult> RepairAsync(CancellationToken cancellationToken = default)
    {
        var before = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        // A wedged daemon can retain its process guard after IPC has failed. Repair must still
        // stop that process before rewriting/restarting the registration.
        var startAfterRepair = before.IsReachable || IsInstanceGuardHeld();
        return await ExecuteAsync(async lifecycle =>
        {
            await lifecycle.CleanupLegacyRegistrationsAsync(cancellationToken).ConfigureAwait(false);
            if (startAfterRepair)
                await lifecycle.StopAsync(cancellationToken).ConfigureAwait(false);

            // Repair is deliberately split into stop, registration rewrite, and start. In
            // particular, a supervisor stop command can return before an old (or manually
            // launched) process releases the per-user guard. Starting the replacement before
            // that release would race two generations of the daemon.
            await WaitForReachabilityAsync(expected: false, cancellationToken).ConfigureAwait(false);
            await lifecycle.RepairRegistrationAsync(cancellationToken).ConfigureAwait(false);
            if (startAfterRepair)
            {
                // A registration repair must not itself have relaunched the daemon. Recheck at
                // the start boundary so every platform shares the same sequencing invariant.
                await WaitForReachabilityAsync(expected: false, cancellationToken).ConfigureAwait(false);
                await lifecycle.StartAsync(cancellationToken).ConfigureAwait(false);
                await WaitForReachabilityAsync(expected: true, cancellationToken).ConfigureAwait(false);
            }
        }, status => status.RegistrationIsCurrent && status.IsReachable == startAfterRepair,
        "The daemon registration could not be repaired to its previous running state.", cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<DaemonLifecycleResult> UninstallAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(async lifecycle =>
        {
            await lifecycle.UninstallAsync(cancellationToken).ConfigureAwait(false);
            await WaitForReachabilityAsync(expected: false, cancellationToken).ConfigureAwait(false);
        }, status => !status.IsInstalled && !status.IsReachable,
        "The daemon process or registration is still present after uninstall.", cancellationToken);

    public Task<DaemonLifecycleResult> CleanupLegacyRegistrationsAsync(
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            lifecycle => lifecycle.CleanupLegacyRegistrationsAsync(cancellationToken),
            _ => true,
            "Legacy daemon registrations could not be removed.",
            cancellationToken);

    private async Task<DaemonLifecycleResult> ExecuteAsync(
        Func<IDaemonPlatformLifecycle, Task> action,
        Func<DaemonLifecycleStatus, bool> succeeded,
        string incompleteMessage,
        CancellationToken cancellationToken)
    {
        if (_platformLifecycle is null)
        {
            var unsupported = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return new DaemonLifecycleResult(false, unsupported, unsupported.Message);
        }

        try
        {
            // One lifecycle mutation at a time per user, across processes: concurrent dashboard
            // tabs, extra root backends, and the installer CLI must not interleave their
            // stop/rewrite/start sequences against the same OS registration.
            using var lifecycleLock = await DaemonLifecycleLock.AcquireAsync(
                _scoped.Registration.ApplicationId,
                _scoped.Identity,
                _scoped.Registration.DataDirectory,
                LifecycleLockTimeout,
                cancellationToken).ConfigureAwait(false);
            await action(_platformLifecycle).ConfigureAwait(false);
            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return succeeded(status)
                ? new DaemonLifecycleResult(true, status)
                : new DaemonLifecycleResult(false, status, incompleteMessage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var status = await GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
            return new DaemonLifecycleResult(false, status, ex.Message);
        }
    }

    private async Task WaitForReachabilityAsync(bool expected, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            var response = await _controlClient.SendAsync(
                _scoped.PipeName,
                DaemonControlCommand.Ping,
                StatusTimeout,
                cancellationToken).ConfigureAwait(false);
            var reachable = response.Outcome == DaemonControlClientOutcome.Success;
            // On shutdown, a missing pipe is not enough: a wedged process may have stopped
            // accepting IPC while it still holds the executable open. The same per-user guard the
            // daemon acquires before starting its host is the authoritative process-lifetime bit.
            var instanceStopped = expected || !IsInstanceGuardHeld();
            if (reachable == expected && instanceStopped)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        } while (started.Elapsed < TransitionTimeout);

        throw new TimeoutException(expected
            ? "The daemon did not become reachable within the expected time."
            : "The daemon process did not release its current-user instance guard within the expected time.");
    }

    private bool IsInstanceGuardHeld() => DaemonInstanceGuard.IsHeld(
        _scoped.Registration.ApplicationId,
        _scoped.Identity,
        _scoped.Registration.DataDirectory);
}
