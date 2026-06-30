using VibeRails.DTOs;

namespace VibeRails.Services.AgentTools;

public sealed class LocalAgentTerminalToolGateway : IAgentTerminalToolGateway
{
    private readonly IAgentTerminalToolService _tools;

    public LocalAgentTerminalToolGateway(IAgentTerminalToolService tools)
    {
        _tools = tools;
    }

    public Task<AgentToolTerminalListResponse> ListTerminalsAsync(CancellationToken cancellationToken = default) =>
        _tools.ListTerminalsAsync(cancellationToken);

    public Task<TerminalTabStatusResponse> OpenTerminalAsync(AgentToolOpenTerminalRequest request, CancellationToken cancellationToken = default) =>
        _tools.OpenTerminalAsync(request, cancellationToken);

    public Task<TerminalInputResponse> SendInputAsync(string? tabId, TerminalInputRequest request, CancellationToken cancellationToken = default) =>
        _tools.SendInputAsync(tabId, request, cancellationToken);

    public Task<TerminalSnapshotResponse?> CaptureSnapshotAsync(string? tabId, CancellationToken cancellationToken = default) =>
        _tools.CaptureSnapshotAsync(tabId, cancellationToken);
}
