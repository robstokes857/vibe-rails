using System.Text.Json;
using VibeRails.Daemon;
using VibeRails.Daemon.Ipc;
using VibeRails.Daemon.Platform;
using VibeRails.DTOs;

namespace VibeRails.Services.Jobs;

internal sealed class JobDaemonLifecycleService : IJobDaemonLifecycleService
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromMilliseconds(750);

    private readonly IJobDaemonRegistrationProvider _registrationProvider;
    private readonly IDaemonControlClient _controlClient;
    private readonly ICurrentUserIdentityProvider _identityProvider;
    private readonly CurrentUserIdentity _identity;
    private readonly string _pipeName;

    public JobDaemonLifecycleService(
        IJobDaemonRegistrationProvider registrationProvider,
        IDaemonControlClient controlClient,
        ICurrentUserIdentityProvider identityProvider)
    {
        _registrationProvider = registrationProvider;
        _controlClient = controlClient;
        _identityProvider = identityProvider;
        _identity = identityProvider.GetCurrent();
        _pipeName = JobDaemonRegistrationFactory.GetPipeName(_identity);
    }

    public async Task<JobDaemonStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // Resolved per call, not latched at construction: the launch target can be installed or
        // deleted while the dashboard stays open, and the panel must follow reality.
        var resolution = _registrationProvider.Current;
        var manager = CreateLifecycle(resolution);
        if (manager is null)
            return await UnavailableStatusWithLivenessAsync(resolution, cancellationToken).ConfigureAwait(false);

        var lifecycle = await manager.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var instanceGuardHeld = IsInstanceGuardHeld(resolution);
        JobDaemonControlStatusPayload? payload = null;
        string? liveError = null;
        var liveOutcome = DaemonControlClientOutcome.Unreachable;

        if (lifecycle.IsReachable)
        {
            var live = await _controlClient.SendAsync(
                _pipeName,
                DaemonControlCommand.Status,
                StatusTimeout,
                cancellationToken).ConfigureAwait(false);
            liveOutcome = live.Outcome;
            if (live.Outcome == DaemonControlClientOutcome.Success && live.Response?.Payload is { } element)
            {
                try
                {
                    payload = JsonSerializer.Deserialize(
                        element.GetRawText(),
                        JobDaemonControlJsonContext.Default.JobDaemonControlStatusPayload);
                    if (payload is null)
                        liveError = "VBD returned an empty status payload.";
                }
                catch (JsonException ex)
                {
                    liveError = $"VBD returned an invalid status payload: {ex.Message}";
                }
            }
            else
            {
                liveError = live.Error ?? "VBD did not return a live status payload.";
            }
        }

        var state = MapState(lifecycle.State);
        var versionMismatch = payload is not null &&
            !string.Equals(payload.Version, VersionInfo.Version, StringComparison.OrdinalIgnoreCase);
        var protocolMismatch = payload is not null &&
            payload.ProtocolVersion != DaemonControlProtocol.Version;
        var invalidLiveStatus = lifecycle.IsReachable &&
            (liveOutcome != DaemonControlClientOutcome.Success || payload is null);
        var missingLaunchTarget = !resolution.LaunchTargetExists;

        if (versionMismatch || protocolMismatch || invalidLiveStatus ||
            (instanceGuardHeld && !lifecycle.IsReachable) ||
            (missingLaunchTarget && (lifecycle.IsInstalled || lifecycle.IsReachable || instanceGuardHeld)))
        {
            state = JobDaemonState.NeedsRepair;
        }
        else if (missingLaunchTarget && !lifecycle.IsInstalled)
        {
            state = JobDaemonState.Unavailable;
        }

        var repairReason = versionMismatch
            ? $"VBD {payload!.Version} does not match this VibeRails version ({VersionInfo.Version})."
            : protocolMismatch
                ? $"VBD protocol {payload!.ProtocolVersion} does not match protocol {DaemonControlProtocol.Version}."
                : invalidLiveStatus
                    ? liveError
                    : instanceGuardHeld && !lifecycle.IsReachable
                        ? "A VBD process is still running, but its control pipe is unreachable."
                    : missingLaunchTarget
                        ? $"The stable VBD launch target is missing: {resolution.LaunchTargetPath}"
                        : null;

        var error = repairReason
            ?? lifecycle.Error
            ?? (state is JobDaemonState.NeedsRepair or JobDaemonState.Error ? lifecycle.Message : null)
            ?? payload?.LastError;

        var platform = PlatformName(lifecycle.Platform);
        var isSupported = lifecycle.IsSupported && state != JobDaemonState.Unavailable;
        return new JobDaemonStatusResponse(
            state,
            platform,
            isSupported,
            lifecycle.IsInstalled,
            IsRunning: lifecycle.IsReachable || instanceGuardHeld,
            lifecycle.IsReachable,
            lifecycle.RegistrationIsCurrent,
            VersionInfo.Version,
            payload?.Version,
            payload?.ProtocolVersion,
            payload?.Pid,
            payload?.StartedUtc,
            payload?.UptimeSeconds,
            payload?.LastCycleUtc,
            // Null when VBD is stopped or unreachable: the frontend's tri-state renders that as
            // "Not reported" instead of the false claim "Owned elsewhere".
            payload?.OwnsSchedulerLease,
            error,
            PlatformLimitation(lifecycle.Platform, missingLaunchTarget && !lifecycle.IsInstalled),
            AllowedActions(state, lifecycle.IsInstalled, lifecycle.IsReachable || instanceGuardHeld));
    }

    public Task<JobDaemonActionResponse> InstallAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("installed", requireLaunchTarget: true, manager => manager.InstallAsync(cancellationToken), cancellationToken);

    public Task<JobDaemonActionResponse> StartAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("started", requireLaunchTarget: true, manager => manager.StartAsync(cancellationToken), cancellationToken);

    public Task<JobDaemonActionResponse> StopAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("stopped", requireLaunchTarget: false, manager => manager.StopAsync(cancellationToken), cancellationToken);

    public Task<JobDaemonActionResponse> RestartAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("restarted", requireLaunchTarget: true, manager => manager.RestartAsync(cancellationToken), cancellationToken);

    public Task<JobDaemonActionResponse> RepairAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("repaired", requireLaunchTarget: true, manager => manager.RepairAsync(cancellationToken), cancellationToken);

    public Task<JobDaemonActionResponse> UninstallAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("removed", requireLaunchTarget: false, manager => manager.UninstallAsync(cancellationToken), cancellationToken);

    private async Task<JobDaemonActionResponse> ExecuteAsync(
        string completedAction,
        bool requireLaunchTarget,
        Func<IUserDaemonLifecycleManager, Task<DaemonLifecycleResult>> action,
        CancellationToken cancellationToken)
    {
        var resolution = _registrationProvider.Current;
        var manager = CreateLifecycle(resolution);
        if (manager is null)
        {
            var status = await UnavailableStatusWithLivenessAsync(resolution, cancellationToken).ConfigureAwait(false);
            return new JobDaemonActionResponse(false, status.LastError ?? "VBD is unavailable.", status);
        }

        if (requireLaunchTarget && !resolution.LaunchTargetExists)
        {
            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return new JobDaemonActionResponse(
                false,
                $"The stable VBD launch target is missing: {resolution.LaunchTargetPath}",
                status);
        }

        var result = await action(manager).ConfigureAwait(false);
        var current = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var message = result.Success
            ? $"VibeRails Demon {completedAction}."
            : result.Error ?? result.Status.Error ?? $"VibeRails Demon could not be {completedAction}.";
        return new JobDaemonActionResponse(result.Success, message, current);
    }

    private IUserDaemonLifecycleManager? CreateLifecycle(JobDaemonRegistrationResolution resolution) =>
        resolution.Registration is null
            ? null
            : new UserDaemonLifecycleManager(
                resolution.Registration,
                _identityProvider,
                controlClient: _controlClient);

    /// <summary>
    /// Even when the registration cannot be resolved (custom install dir, failed path validation),
    /// a previously installed daemon may still be running. Installers decide whether an update is
    /// safe from this response, so report pipe reachability and the instance guard truthfully
    /// instead of a blanket "nothing is running".
    /// </summary>
    private async Task<JobDaemonStatusResponse> UnavailableStatusWithLivenessAsync(
        JobDaemonRegistrationResolution resolution,
        CancellationToken cancellationToken)
    {
        var reachable = false;
        try
        {
            var ping = await _controlClient.SendAsync(
                _pipeName,
                DaemonControlCommand.Ping,
                StatusTimeout,
                cancellationToken).ConfigureAwait(false);
            reachable = ping.Outcome == DaemonControlClientOutcome.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Reachability stays false; the unavailable error below remains the headline.
        }

        var guardHeld = IsInstanceGuardHeld(resolution);
        return UnavailableStatus(
            resolution.Error,
            isRunning: reachable || guardHeld,
            isReachable: reachable);
    }

    private JobDaemonStatusResponse UnavailableStatus(
        string? error,
        bool isRunning = false,
        bool isReachable = false) => new(
        JobDaemonState.Unavailable,
        PlatformName(new RuntimeDaemonPlatformProvider().Current),
        IsSupported: false,
        IsInstalled: false,
        IsRunning: isRunning,
        IsReachable: isReachable,
        RegistrationIsCurrent: false,
        CurrentVersion: VersionInfo.Version,
        LastError: error ?? "VibeRails Demon lifecycle support is unavailable in this build.",
        PlatformLimitation: error,
        AllowedActions: []);

    private static JobDaemonState MapState(DaemonLifecycleState state) => state switch
    {
        DaemonLifecycleState.NotInstalled => JobDaemonState.NotInstalled,
        DaemonLifecycleState.InstalledStopped => JobDaemonState.InstalledStopped,
        DaemonLifecycleState.Running => JobDaemonState.Running,
        DaemonLifecycleState.NeedsRepair => JobDaemonState.NeedsRepair,
        DaemonLifecycleState.Unavailable => JobDaemonState.Unavailable,
        _ => JobDaemonState.Error
    };

    private static string PlatformName(DaemonPlatformKind platform) => platform switch
    {
        DaemonPlatformKind.Windows => "Windows Task Scheduler",
        DaemonPlatformKind.Linux => "Linux systemd user service",
        DaemonPlatformKind.MacOS => "macOS LaunchAgent",
        _ => "Unsupported platform"
    };

    private static string PlatformLimitation(DaemonPlatformKind platform, bool missingLaunchTarget) =>
        missingLaunchTarget
            ? "Install or update VibeRails in ~/.vibe_rails before enabling background Automations."
            : platform switch
            {
                DaemonPlatformKind.Windows =>
                    "Requires a signed-in user and interactive desktop; sleep or logout pauses visible Automation launches.",
                DaemonPlatformKind.Linux =>
                    "Uses systemd --user and requires an interactive graphical login for visible Automation terminals.",
                DaemonPlatformKind.MacOS =>
                    "Uses a per-user LaunchAgent and requires a logged-in graphical session for visible Automation terminals.",
                _ => "Current-user background Automations are not supported on this operating system."
            };

    private static IReadOnlyList<string> AllowedActions(
        JobDaemonState state,
        bool installed,
        bool reachable) => state switch
    {
        JobDaemonState.NotInstalled => ["install"],
        JobDaemonState.InstalledStopped => ["start", "repair", "remove"],
        JobDaemonState.Running => ["stop", "restart", "repair", "remove"],
        JobDaemonState.NeedsRepair when reachable => ["stop", "repair", "remove"],
        JobDaemonState.NeedsRepair when installed => ["repair", "remove"],
        JobDaemonState.Error when reachable => ["stop", "repair", "remove"],
        JobDaemonState.Error when installed => ["repair", "remove"],
        _ => []
    };

    private bool IsInstanceGuardHeld(JobDaemonRegistrationResolution resolution)
    {
        try
        {
            return DaemonInstanceGuard.IsHeld(
                JobDaemonRegistrationFactory.ApplicationId,
                _identity,
                resolution.Registration?.DataDirectory ?? resolution.InstallDirectory);
        }
        catch
        {
            // IPC and the OS lifecycle inspection still provide a useful degraded status.
            return false;
        }
    }
}
