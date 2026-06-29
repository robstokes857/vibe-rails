using VibeRails.DTOs;

namespace VibeRails.Services.AgentTools;

public interface IAgentTerminalToolService
{
    Task<AgentToolTerminalListResponse> ListTerminalsAsync(CancellationToken cancellationToken = default);
    Task<TerminalTabStatusResponse> OpenTerminalAsync(AgentToolOpenTerminalRequest request, CancellationToken cancellationToken = default);
    Task<TerminalInputResponse> SendInputAsync(string? tabId, TerminalInputRequest request, CancellationToken cancellationToken = default);
    Task<TerminalSnapshotResponse?> CaptureSnapshotAsync(string? tabId, CancellationToken cancellationToken = default);
}
