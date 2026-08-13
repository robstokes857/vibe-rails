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
    /// <param name="resolveInitialPrompt">
    /// Produces the Initial Message, invoked only once this call owns the single-session slot.
    /// A delegate rather than a string because resolving one has side effects — {{step:&lt;id&gt;}}
    /// runs a shell command — and two concurrent start requests must not both run them when only
    /// one of them can go on to own a terminal. Null when there is no Initial Message.
    /// </param>
    Task<bool> StartSessionAsync(LLM llm, string workingDirectory, string? environmentName = null, string[]? extraArgs = null, string? title = null, bool makeRemote = false, Func<Task<string?>>? resolveInitialPrompt = null, string summary = "");
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
