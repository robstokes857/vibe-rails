using System.Net.WebSockets;
using VibeRails.DTOs;

namespace VibeRails.Services.Terminal;

public interface ITerminalSessionService
{
    bool HasActiveSession { get; }
    string? ActiveSessionId { get; }
    string? ActiveCli { get; }
    string? ActiveWorkingDirectory { get; }
    bool IsExternallyOwned { get; }
    Task<bool> StartSessionAsync(LLM llm, string workingDirectory, string? environmentName = null, string[]? extraArgs = null, string? title = null, bool makeRemote = false, string? initialPrompt = null, string summary = "");
    Task HandleWebSocketAsync(WebSocket webSocket, CancellationToken cancellationToken, int? cols = null, int? rows = null);
    Task<TerminalInputResponse> SendInputAsync(TerminalInputRequest request, CancellationToken cancellationToken = default);
    Task StopSessionAsync();
    void RegisterExternalTerminal(Terminal terminal, string sessionId, string workingDirectory);
    Task UnregisterTerminalAsync();
    Task DisconnectLocalViewerAsync(string reason);
    Task<bool> SendRemoteCommandAsync(string command, string? payload = null, CancellationToken cancellationToken = default);
    Task<TerminalSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default);
    Task<TerminalImageCaptureResult> CaptureImageSnapshotAsync(CancellationToken cancellationToken = default);
}
