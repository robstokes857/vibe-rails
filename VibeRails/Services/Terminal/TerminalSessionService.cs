using System.Net.WebSockets;
using Serilog;
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
    Task<bool> StartSessionAsync(LLM llm, string workingDirectory, string? environmentName = null, string[]? extraArgs = null, string? title = null, bool makeRemote = false);
    Task HandleWebSocketAsync(WebSocket webSocket, CancellationToken cancellationToken);
    Task StopSessionAsync();
    void RegisterExternalTerminal(Terminal terminal, string sessionId);
    Task UnregisterTerminalAsync();
    Task DisconnectLocalViewerAsync(string reason);
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

    public TerminalSessionService(
        IDbService dbService,
        LlmCliEnvironmentService envService,
        IGitService gitService,
        McpSettings mcpSettings,
        IRemoteStateService remoteStateService,
        ITerminalIoObserverService ioObserverService)
    {
        _stateService = new TerminalStateService(dbService, gitService, remoteStateService, ioObserverService);
        _runner = new TerminalRunner(_stateService, envService, mcpSettings);
    }

    public async Task<bool> StartSessionAsync(LLM llm, string workingDirectory, string? environmentName = null, string[]? extraArgs = null, string? title = null, bool makeRemote = false)
    {
        lock (s_lock)
        {
            if (s_terminal != null) return false;
        }

        try
        {
            var (terminal, sessionId, remoteConn) = await _runner.CreateSessionAsync(
                llm, workingDirectory, environmentName, extraArgs, CancellationToken.None, title, makeRemote);

            // When a remote browser connects, disconnect the local WebUI viewer
            if (remoteConn != null)
            {
                remoteConn.OnReplayRequested += () =>
                {
                    _ = DisconnectLocalViewerAsync("Session taken over by remote viewer");
                };
            }

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
            Log.Error(ex, "[Terminal] Failed to start session");
            await CleanupAsync();
            throw;
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

        // Local viewer connected: enforce single-viewer mode by disconnecting any
        // remote browser currently attached through the relay.
        try
        {
            await _stateService.RequestRemoteViewerDisconnectAsync(sessionId, "Session taken over by local viewer");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Terminal] Failed to disconnect remote viewer");
        }

        // Replay buffered output so new viewer sees current screen state
        var replay = terminal.GetReplayBuffer();
        if (replay.Length > 0)
        {
            try
            {
                await webSocket.SendAsync(replay, WebSocketMessageType.Binary, true, cancellationToken);
                Log.Information("[Terminal] Replayed {Bytes} bytes of buffered output to new viewer", replay.Length);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Terminal] Failed to replay buffer");
            }
        }

        // Subscribe WebSocket as output consumer
        using var wsConsumer = new WebSocketConsumer(webSocket, cancellationToken);
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

    public async Task DisconnectLocalViewerAsync(string reason)
    {
        WebSocket? wsToClose;
        lock (s_lock)
        {
            wsToClose = s_activeWebSocket;
            s_activeWebSocket = null;
        }

        Log.Information("[Terminal] DisconnectLocalViewerAsync called: reason={Reason}, hasWebSocket={HasWs}, state={State}", reason, wsToClose != null, wsToClose?.State);

        if (wsToClose?.State == WebSocketState.Open)
        {
            try
            {
                await wsToClose.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
                Log.Information("[Terminal] Local viewer disconnected successfully");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Terminal] Error disconnecting local viewer");
            }
        }
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
                if (result.Count <= 0) continue;

                byte[] inputBytes;
                if (result.EndOfMessage)
                {
                    if (result.Count > TerminalControlProtocol.MaxMessageBytes)
                    {
                        Log.Warning("[Terminal] Viewer message exceeded limit ({Bytes} bytes)", result.Count);
                        break;
                    }
                    inputBytes = buffer[..result.Count].ToArray();
                }
                else
                {
                    using var ms = new MemoryStream();
                    ms.Write(buffer, 0, result.Count);
                    if (ms.Length > TerminalControlProtocol.MaxMessageBytes)
                    {
                        Log.Warning("[Terminal] Viewer fragmented message exceeded limit ({Bytes} bytes)", ms.Length);
                        break;
                    }
                    while (!result.EndOfMessage)
                    {
                        result = await webSocket.ReceiveAsync(buffer, ct);
                        ms.Write(buffer, 0, result.Count);
                        if (ms.Length > TerminalControlProtocol.MaxMessageBytes)
                        {
                            Log.Warning("[Terminal] Viewer fragmented message exceeded limit ({Bytes} bytes)", ms.Length);
                            break;
                        }
                    }
                    if (ms.Length > TerminalControlProtocol.MaxMessageBytes)
                        break;
                    inputBytes = ms.ToArray();
                }

                var input = System.Text.Encoding.UTF8.GetString(inputBytes);

                // Reserved control message from browser to keep PTY dimensions in sync.
                if (result.MessageType == WebSocketMessageType.Text &&
                    TerminalControlProtocol.TryParseResizeCommand(input, out var cols, out var rows))
                {
                    try
                    {
                        terminal.Resize(cols, rows);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[Terminal] Failed to resize PTY to {Cols}x{Rows}", cols, rows);
                    }
                    continue;
                }

                await TerminalIoRouter.RouteInputAsync(
                    stateService,
                    terminal,
                    sessionId,
                    inputBytes,
                    TerminalIoSource.LocalWebUi,
                    ct);
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
                Log.Information("[Terminal] WebSocket takeover - disconnecting previous viewer");
                await oldWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session taken over", CancellationToken.None);
                Log.Information("[Terminal] Old WebSocket closed successfully");
            }
            else
            {
                Log.Information("[Terminal] Old WebSocket was already in state: {State}", oldWebSocket.State);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Terminal] Error closing old WebSocket");
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
