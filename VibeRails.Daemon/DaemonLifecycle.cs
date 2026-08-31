using VibeRails.Daemon.Ipc;

namespace VibeRails.Daemon;

public enum DaemonLifecycleState
{
    NotInstalled,
    InstalledStopped,
    Running,
    NeedsRepair,
    Unavailable,
    Error
}

public enum DaemonPlatformKind
{
    Windows,
    Linux,
    MacOS,
    Unsupported
}

public sealed record DaemonLifecycleStatus(
    DaemonLifecycleState State,
    DaemonPlatformKind Platform,
    bool IsSupported,
    bool IsInstalled,
    bool IsReachable,
    bool RegistrationIsCurrent,
    string? Message = null,
    string? Error = null);

public sealed record DaemonLifecycleResult(
    bool Success,
    DaemonLifecycleStatus Status,
    string? Error = null);

public interface IUserDaemonLifecycleManager
{
    Task<DaemonLifecycleStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<DaemonLifecycleResult> InstallAsync(CancellationToken cancellationToken = default);
    Task<DaemonLifecycleResult> StartAsync(CancellationToken cancellationToken = default);
    Task<DaemonLifecycleResult> StopAsync(CancellationToken cancellationToken = default);
    Task<DaemonLifecycleResult> RestartAsync(CancellationToken cancellationToken = default);
    Task<DaemonLifecycleResult> RepairAsync(CancellationToken cancellationToken = default);
    Task<DaemonLifecycleResult> UninstallAsync(CancellationToken cancellationToken = default);
    Task<DaemonLifecycleResult> CleanupLegacyRegistrationsAsync(CancellationToken cancellationToken = default);
}

public interface IDaemonPlatformProvider
{
    DaemonPlatformKind Current { get; }
}

public sealed class RuntimeDaemonPlatformProvider : IDaemonPlatformProvider
{
    public DaemonPlatformKind Current => OperatingSystem.IsWindows()
        ? DaemonPlatformKind.Windows
        : OperatingSystem.IsLinux()
            ? DaemonPlatformKind.Linux
            : OperatingSystem.IsMacOS()
                ? DaemonPlatformKind.MacOS
                : DaemonPlatformKind.Unsupported;
}

internal enum DaemonRegistrationCondition
{
    NotInstalled,
    Current,
    Stale,
    Unavailable,
    Error
}

internal sealed record DaemonRegistrationInspection(
    DaemonRegistrationCondition Condition,
    string? Message = null,
    string? Error = null);

internal static class DaemonLifecycleStatusMapper
{
    public static DaemonLifecycleStatus Map(
        DaemonPlatformKind platform,
        DaemonRegistrationInspection registration,
        DaemonControlClientResult reachability)
    {
        if (platform == DaemonPlatformKind.Unsupported ||
            registration.Condition == DaemonRegistrationCondition.Unavailable)
        {
            return new DaemonLifecycleStatus(
                DaemonLifecycleState.Unavailable,
                platform,
                IsSupported: false,
                IsInstalled: false,
                IsReachable: false,
                RegistrationIsCurrent: false,
                Message: registration.Message,
                Error: registration.Error);
        }

        if (registration.Condition == DaemonRegistrationCondition.Error)
        {
            return new DaemonLifecycleStatus(
                DaemonLifecycleState.Error,
                platform,
                IsSupported: true,
                IsInstalled: false,
                IsReachable: reachability.IsReachable,
                RegistrationIsCurrent: false,
                Message: registration.Message,
                Error: registration.Error ?? reachability.Error);
        }

        var installed = registration.Condition != DaemonRegistrationCondition.NotInstalled;
        var current = registration.Condition == DaemonRegistrationCondition.Current;
        var reachable = reachability.IsReachable;
        var mismatch = reachability.Outcome is
            DaemonControlClientOutcome.Rejected or
            DaemonControlClientOutcome.ProtocolMismatch or
            DaemonControlClientOutcome.InvalidResponse;

        var state = registration.Condition == DaemonRegistrationCondition.Stale || mismatch ||
                    (reachable && !installed)
            ? DaemonLifecycleState.NeedsRepair
            : current && reachability.Outcome == DaemonControlClientOutcome.Success
                ? DaemonLifecycleState.Running
                : current
                    ? DaemonLifecycleState.InstalledStopped
                    : DaemonLifecycleState.NotInstalled;

        return new DaemonLifecycleStatus(
            state,
            platform,
            IsSupported: true,
            IsInstalled: installed,
            IsReachable: reachable,
            RegistrationIsCurrent: current,
            Message: registration.Message,
            Error: mismatch ? reachability.Error : registration.Error);
    }
}
