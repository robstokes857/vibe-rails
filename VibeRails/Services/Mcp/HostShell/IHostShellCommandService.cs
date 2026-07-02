namespace VibeRails.Services.Mcp.HostShell;

public interface IHostShellCommandService
{
    Task<HostShellCommandResult> RunAsync(HostShellCommandRequest request, CancellationToken cancellationToken = default);
    HostShellCommandResult? GetStatus(string jobId);
    Task<HostShellCommandResult?> CancelAsync(string jobId, CancellationToken cancellationToken = default);
}

