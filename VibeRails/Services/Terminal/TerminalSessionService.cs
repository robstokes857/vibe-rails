using System.Net.WebSockets;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.LlmClis;
using VibeRails.Services.Terminal.Consumers;

namespace VibeRails.Services.Terminal;

public interface ITerminalSessionService
{
    bool HasActiveSession { get; }
    string? ActiveSessionId { get; }
    bool IsExternallyOwned { get; }
    Task<bool> StartSessionAsync(LLM llm, string workingDirectory, string? environmentName = null, string[]? extraArgs = null, string? title = null);
    Task HandleWebSocketAsync(WebSocket webSocket, CancellationToken cancellationToken);
    Task StopSessionAsync();
    void RegisterExternalTerminal(Terminal terminal, string sessionId);
    Task UnregisterTerminalAsync();
}

public class TerminalSessionService : ITerminalSessionService
{
    private readonly TerminalRunner _runner;
    private readonly ITerminalStateService _stateService;

    private static readonly Lock s_lock = new();
    private static Terminal? s_terminal;
    private static string? s_sessionId;
    private static WebSocket? s_activeWebSocket;
    private static bool s_externallyOwned;

    public bool HasActiveSession => s_terminal != null;
    public string? ActiveSessionId => s_sessionId;
    public bool IsExternallyOwned { get { lock (s_lock) return s_externallyOwned; } }

    public TerminalSessionService(IDbService dbService, LlmCliEnvironmentService envService, IGitService gitService, McpSettings mcpSettings, IRemoteStateService remoteStateService)
    {
        _stateService = new TerminalStateService(dbService, gitService, remoteStateService);
        _runner = new TerminalRunner(_stateService, envService, mcpSettings);
    }

    public async Task<bool> StartSessionAsync(LLM llm, string workingDirectory, string? environmentName = null, string[]? extraArgs = null, string? title = null)
    {
        lock (s_lock)
        {
            if (s_terminal != null) return false;
        }

        try
        {
            var (terminal, sessionId) = await _runner.CreateSessionAsync(
                llm, workingDirectory, environmentName, extraArgs, CancellationToken.None, title);

            // Subscribe to terminal exit event
            terminal.Exited += async (sender, exitCode) =>
            {
                var capturedSessionId = sessionId;
                await _stateService.CompleteSessionAsync(capturedSessionId, exitCode);
                await CleanupAsync();
            };

            // Start the read loop (DB logging consumer is already wired by CreateSessionAsync)
            terminal.StartReadLoop();

            lock (s_lock)
            {
                s_terminal = terminal;
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
        Terminal? terminal;
        string? sessionId;

        lock (s_lock)
        {
            terminal = s_terminal;
            sessionId = s_sessionId;
        }

        if (terminal == null || sessionId == null)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "No active terminal session", cancellationToken);
            return;
        }

        // Handle WebSocket takeover
        await HandleTakeoverAsync(webSocket);

        // Replay buffered output so new viewer sees current screen state
        var replay = terminal.GetReplayBuffer();
        if (replay.Length > 0)
        {
            try
            {
                await webSocket.SendAsync(replay, WebSocketMessageType.Binary, true, cancellationToken);
                Console.WriteLine($"[Terminal] Replayed {replay.Length} bytes of buffered output to new viewer");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Terminal] Failed to replay buffer: {ex.Message}");
            }
        }

        // Send Ctrl+L for screen redraw after buffer replay
        try
        {
            await terminal.WriteBytesAsync(new byte[] { 0x0C }, cancellationToken);
            Console.WriteLine("[Terminal] Sent Ctrl+L to trigger screen refresh for new viewer");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Terminal] Failed to send Ctrl+L: {ex.Message}");
        }

        // Subscribe WebSocket as output consumer
        var wsConsumer = new WebSocketConsumer(webSocket, cancellationToken);
        using var subscription = terminal.Subscribe(wsConsumer);

        // Run WebSocket input loop (blocks until WebSocket closes or cancellation)
        try
        {
            await WebSocketInputLoopAsync(terminal, webSocket, _stateService, sessionId, cancellationToken);
        }
        finally
        {
            // subscription.Dispose() auto-unsubscribes the WebSocketConsumer
            lock (s_lock) { s_activeWebSocket = null; }
            // Don't call CleanupAsync() here - PTY should survive WebSocket disconnects
        }
    }

    public void RegisterExternalTerminal(Terminal terminal, string sessionId)
    {
        lock (s_lock)
        {
            if (s_terminal != null)
                throw new InvalidOperationException("A terminal session is already active");
            s_terminal = terminal;
            s_sessionId = sessionId;
            s_externallyOwned = true;
        }
    }

    public async Task UnregisterTerminalAsync()
    {
        WebSocket? wsToClose;
        lock (s_lock)
        {
            s_terminal = null;
            s_sessionId = null;
            s_externallyOwned = false;
            wsToClose = s_activeWebSocket;
            s_activeWebSocket = null;
        }

        if (wsToClose?.State == WebSocketState.Open)
        {
            try
            {
                var msg = System.Text.Encoding.UTF8.GetBytes("\r\n\x1b[33m[CLI session ended]\x1b[0m\r\n");
                await wsToClose.SendAsync(msg, WebSocketMessageType.Binary, true, CancellationToken.None);
                await Task.Delay(100);
                await wsToClose.CloseAsync(WebSocketCloseStatus.NormalClosure, "CLI session ended", CancellationToken.None);
            }
            catch { }
        }
    }

    public async Task StopSessionAsync()
    {
        string? sessionId;
        lock (s_lock)
        {
            if (s_externallyOwned) return;
            sessionId = s_sessionId;
        }

        // Complete the session before cleanup
        if (sessionId != null)
        {
            await _stateService.CompleteSessionAsync(sessionId, 0);
        }

        await CleanupAsync();
    }

    private static async Task WebSocketInputLoopAsync(
        Terminal terminal, WebSocket webSocket, ITerminalStateService stateService, string sessionId, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.Count > 0)
                {
                    // Log input to DB
                    var input = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                    stateService.RecordInput(sessionId, input);

                    await terminal.WriteBytesAsync(buffer.AsMemory(0, result.Count), ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            if (webSocket.State == WebSocketState.Open)
                try { await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None); } catch { }
        }
    }

    private static async Task HandleTakeoverAsync(WebSocket newWebSocket)
    {
        WebSocket? oldWebSocket;
        lock (s_lock)
        {
            oldWebSocket = (s_activeWebSocket != null && s_activeWebSocket != newWebSocket)
                ? s_activeWebSocket
                : null;
            s_activeWebSocket = newWebSocket;
        }

        if (oldWebSocket == null) return;

        try
        {
            if (oldWebSocket.State == WebSocketState.Open)
            {
                Console.WriteLine("[Terminal] WebSocket takeover - disconnecting previous viewer");

                const string message = "\r\n\x1b[33m[Session taken over by another viewer]\x1b[0m\r\n";
                var messageBytes = System.Text.Encoding.UTF8.GetBytes(message);
                await oldWebSocket.SendAsync(messageBytes, WebSocketMessageType.Binary, true, CancellationToken.None);

                await Task.Delay(100);

                await oldWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session taken over", CancellationToken.None);
                Console.WriteLine("[Terminal] Old WebSocket closed successfully");
            }
            else
            {
                Console.WriteLine($"[Terminal] Old WebSocket was already in state: {oldWebSocket.State}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Terminal] Error closing old WebSocket: {ex.Message}");
        }
    }

    private static async Task CleanupAsync()
    {
        Terminal? terminalToDispose;
        lock (s_lock)
        {
            terminalToDispose = s_externallyOwned ? null : s_terminal;
            s_terminal = null;
            s_sessionId = null;
        }

        if (terminalToDispose != null)
        {
            await terminalToDispose.DisposeAsync();
        }
    }
}
