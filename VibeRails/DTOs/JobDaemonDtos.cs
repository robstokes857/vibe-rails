using System.Text.Json.Serialization;

namespace VibeRails.DTOs;

[JsonConverter(typeof(JsonStringEnumConverter<JobDaemonState>))]
public enum JobDaemonState
{
    NotInstalled,
    InstalledStopped,
    Running,
    NeedsRepair,
    Unavailable,
    Error
}

public sealed record JobDaemonStatusResponse(
    JobDaemonState State,
    string Platform,
    bool IsSupported,
    bool IsInstalled,
    bool IsRunning,
    bool IsReachable,
    bool RegistrationIsCurrent,
    string CurrentVersion,
    string? DaemonVersion = null,
    int? ProtocolVersion = null,
    int? Pid = null,
    DateTime? StartedUtc = null,
    double? UptimeSeconds = null,
    DateTime? LastCycleUtc = null,
    // Tri-state on purpose: null means "VBD did not report" (stopped/unreachable), which the
    // frontend renders as "Not reported" rather than the false claim "Owned elsewhere".
    bool? OwnsSchedulerLease = null,
    string? LastError = null,
    string? PlatformLimitation = null,
    IReadOnlyList<string>? AllowedActions = null);

public sealed record JobDaemonActionResponse(
    bool Success,
    string Message,
    JobDaemonStatusResponse Status);
