using System.Net.WebSockets;
using VibeRails.DTOs;

namespace VibeRails.Services.Terminal;

public interface ITerminalTabHostService
{
    int MaxTabs { get; }
    Task<IReadOnlyList<TerminalTabStatusResponse>> ListTabsAsync(CancellationToken cancellationToken = default);
    Task<TerminalTabStatusResponse> CreateTabAsync(CancellationToken cancellationToken = default);
    Task<TerminalTabStatusResponse> CreateAutomationTabAsync(string runId, CancellationToken cancellationToken = default);
    Task<bool> DeleteTabAsync(string tabId, CancellationToken cancellationToken = default);
    Task<TerminalStatusResponse?> GetStatusAsync(string tabId, CancellationToken cancellationToken = default);
    Task<TerminalStatusResponse> StartSessionAsync(string tabId, StartTerminalRequest request, CancellationToken cancellationToken = default);
    Task<TerminalStatusResponse> StopSessionAsync(string tabId, CancellationToken cancellationToken = default);
    Task<TerminalInputResponse> SendInputAsync(string tabId, TerminalInputRequest request, CancellationToken cancellationToken = default);
    Task<TerminalSnapshotResponse?> CaptureSnapshotAsync(string tabId, CancellationToken cancellationToken = default);
    Task HandleWebSocketProxyAsync(string tabId, WebSocket browserSocket, int? cols = null, int? rows = null, CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
}
