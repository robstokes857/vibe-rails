namespace VibeRails.Services.Mcp.HostShell;

public enum HostShellCommandStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    TimedOut,
    Cancelled
}

public sealed record HostShellCommandRequest(
    string Command,
    string? WorkingDirectory = null,
    string? Shell = null,
    int TimeoutSeconds = 60,
    int WaitSeconds = 30,
    bool WaitForCompletion = true,
    int MaxOutputChars = 20000);

public sealed record HostShellCommandResult(
    string JobId,
    HostShellCommandStatus Status,
    string Shell,
    string WorkingDirectory,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    int? ExitCode,
    string Stdout,
    string Stderr,
    string? Message,
    string? WorkerId);

