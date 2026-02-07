using System.Net.WebSockets;
using Pty.Net;
using VibeRails.Interfaces;
using VibeRails.Services.LlmClis;

namespace VibeRails.Services.Terminal;

public interface ITerminalSessionService
{
    bool HasActiveSession { get; }
    string? ActiveSessionId { get; }
    Task<bool> StartSessionAsync(LLM llm, string workingDirectory, string? environmentName = null, string[]? extraArgs = null);
    Task HandleWebSocketAsync(WebSocket webSocket, CancellationToken cancellationToken);
    Task StopSessionAsync();
}

public class TerminalSessionService : ITerminalSessionService
{
    private readonly TerminalRunner _runner;

    private static readonly object s_lock = new();
    private static IPtyConnection? s_pty;
    private static WebSocket? s_activeWebSocket;
    private static string? s_sessionId;

    public bool HasActiveSession => s_pty != null;
    public string? ActiveSessionId => s_sessionId;

    public TerminalSessionService(IDbService dbService, LlmCliEnvironmentService envService, IGitService gitService)
    {
        var terminalStateService = new TerminalStateService(dbService, gitService);
        _runner = new TerminalRunner(terminalStateService, envService);
    }

    public async Task<bool> StartSessionAsync(LLM llm, string workingDirectory, string? environmentName = null, string[]? extraArgs = null)
    {
        lock (s_lock)
        {
            if (s_pty != null) return false;
        }

        try
        {
            var (pty, sessionId) = await _runner.StartWebAsync(llm, workingDirectory, environmentName, extraArgs, CancellationToken.None);

            lock (s_lock)
            {
                s_pty = pty;
                s_sessionId = sessionId;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Terminal] Failed to start session: {ex.Message}");
            await CleanupAsync();
            return false;
        }
    }

    public async Task HandleWebSocketAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        if (s_pty == null || s_sessionId == null)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "No active terminal session", cancellationToken);
            return;
        }

        lock (s_lock)
        {
            if (s_activeWebSocket != null)
            {
                _ = webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Another client is already connected", cancellationToken);
                return;
            }
            s_activeWebSocket = webSocket;
        }

        try
        {
            await _runner.HandleWebSocketAsync(s_pty, s_sessionId, webSocket, cancellationToken);
        }
        finally
        {
            lock (s_lock) { s_activeWebSocket = null; }
            await CleanupAsync();
        }
    }

    public async Task StopSessionAsync()
    {
        await CleanupAsync();
    }

    private static async Task CleanupAsync()
    {
        lock (s_lock)
        {
            s_pty?.Dispose();
            s_pty = null;
            s_sessionId = null;
        }
        await Task.CompletedTask;
    }
}
