using System.Net.WebSockets;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.Terminal.Consumers;


namespace VibeRails.Services.Terminal;

public sealed record TerminalSnapshot(
    string SessionId,
    DateTimeOffset CapturedUtc,
    int Cols,
    int Rows,
    string[] ScreenText,
    TerminalXtermUiBytes? XtermUiBytes = null,
    string? XtermPngString = null);

public sealed record TerminalImageCaptureResult(
    bool Success,
    byte[]? PngBytes,
    string Message);

public class TerminalSessionService : ITerminalSessionService
{
    private readonly TerminalRunner _runner;
    private readonly ITerminalStateService _stateService;
    private readonly ILocalClientTracker _localClientTracker;

    private static readonly Lock s_lock = new();
    private static readonly SemaphoreSlim s_lifecycleGate = new(1, 1);
    private static Terminal? s_terminal;
    private static string? s_sessionId;
    private static string? s_cli;
    private static string? s_workingDirectory;
    private static WebSocket? s_activeWebSocket;
    private static string? s_sessionOwnerId;
    private static bool s_externallyOwned;

    public bool HasActiveSession
    {
        get
        {
            lock (s_lock)
                return s_terminal != null;
        }
    }

    public string? ActiveSessionId
    {
        get
        {
            lock (s_lock)
                return s_sessionId;
        }
    }

    // CLI of the active session ("Claude", "Codex", ...). Null for externally
    // owned sessions. The web client uses this to apply per-CLI rendering
    // behavior (Codex-only cursor suppression) after a reconnect, when the
    // launch request that carried the CLI is no longer in hand.
    public string? ActiveCli
    {
        get
        {
            lock (s_lock)
                return s_cli;
        }
    }

    public string? ActiveWorkingDirectory
    {
        get
        {
            lock (s_lock)
                return s_workingDirectory;
        }
    }

    public bool IsExternallyOwned { get { lock (s_lock) return s_externallyOwned; } }

    public TerminalSessionService(
        ITerminalStateService stateService,
        TerminalRunner runner,
        ILocalClientTracker localClientTracker)
    {
        _stateService = stateService;
        _runner = runner;
        _localClientTracker = localClientTracker;
    }

    public async Task<bool> StartSessionAsync(LLM llm, string workingDirectory, string? environmentName = null, string[]? extraArgs = null, string? title = null, bool makeRemote = false, Func<Task<string?>>? resolveInitialPrompt = null, string summary = "")
    {
        await s_lifecycleGate.WaitAsync();

        try
        {
            lock (s_lock)
            {
                if (s_terminal != null)
                    return false;
            }

            // Past the gate and past the occupancy check, so this call owns the slot and no
            // concurrent start can also reach here. That is what makes it safe to run the
            // Initial Message's {{step:<id>}} commands: a loser blocks on the gate above, sees
            // the winner's terminal, and returns false without executing anything.
            var initialPrompt = resolveInitialPrompt is null ? null : await resolveInitialPrompt();

            var (terminal, sessionId, _) = await _runner.CreateSessionAsync(
                llm,
                workingDirectory,
                environmentName,
                extraArgs,
                CancellationToken.None,
                title,
                makeRemote,
                initialPrompt,
                summary: summary,
                onRemoteTakeoverAuthorized: trigger =>
                {
                    Log.Information(
                        "[Terminal] Remote viewer authorized via {Trigger} - disconnecting local viewer",
                        trigger);
                    return DisconnectLocalViewerAsync("Session taken over by remote viewer");
                });

            terminal.Exited += (_, exitCode) => ScheduleExitCleanup(terminal, sessionId, exitCode);

            // Start the read loop (DB logging consumer is already wired by CreateSessionAsync)
            terminal.StartReadLoop();

            lock (s_lock)
            {
                s_terminal = terminal;
                s_sessionId = sessionId;
                s_cli = LlmParser.ToWireName(llm);
                s_workingDirectory = workingDirectory;
                s_sessionOwnerId = BuildSessionOwnerId(sessionId);
            }
            _localClientTracker.AcquireOwner(BuildSessionOwnerId(sessionId));

            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Terminal] Failed to start session");
            throw;
        }
        finally
        {
            s_lifecycleGate.Release();
        }
    }

    public async Task HandleWebSocketAsync(WebSocket webSocket, CancellationToken cancellationToken, int? cols = null, int? rows = null)
    {
        Terminal? terminal;
        string? sessionId;
        string? ownerId = null;

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

        // If the client told us its current dimensions, resize the PTY *before*
        // subscribing so the snapshot and any subsequent live output are already
        // at the correct size. Without this, the snapshot arrives at the old size
        // and the subsequent SIGWINCH-triggered redraw writes content a second time.
        if (cols.HasValue && rows.HasValue)
        {
            try
            {
                TerminalResizeCoordinator.ApplyResize(
                    terminal,
                    _stateService,
                    sessionId,
                    cols.Value,
                    rows.Value,
                    TerminalIoSource.LocalWebUi);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Terminal] Failed to pre-resize PTY ({Cols}x{Rows}) before attach", cols, rows);
            }
        }

        // Atomic attach: the snapshot is captured + delivered + the consumer is
        // subscribed, all under the terminal's subscriber lock. The consumer sees
        // the snapshot first and every live PTY byte after that, in order, with
        // no gap and no duplication. No CLI-specific branches, no Ctrl+L poke —
        // we behave like a correct PTY/VT100 terminal.
        using var wsConsumer = new WebSocketConsumer(webSocket, cancellationToken, () => terminal.IsSyncOutputActive);
        using var subscription = terminal.SubscribeWithSnapshot(wsConsumer);
        ownerId = $"terminal-ws:{sessionId}:{Guid.NewGuid():N}";
        _localClientTracker.AcquireOwner(ownerId);

        // Run WebSocket input loop (blocks until WebSocket closes or cancellation)
        try
        {
            await WebSocketInputLoopAsync(terminal, webSocket, wsConsumer, _stateService, sessionId, cancellationToken);
        }
        finally
        {
            // subscription.Dispose() auto-unsubscribes the WebSocketConsumer
            lock (s_lock)
            {
                if (ReferenceEquals(s_activeWebSocket, webSocket))
                {
                    s_activeWebSocket = null;
                }
            }
            if (!string.IsNullOrEmpty(ownerId))
            {
                _localClientTracker.ReleaseOwner(ownerId);
            }
            // Don't call CleanupAsync() here - PTY should survive WebSocket disconnects
        }
    }

    public async Task<TerminalInputResponse> SendInputAsync(TerminalInputRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrEmpty(request.Text))
        {
            return new TerminalInputResponse(false, "Input text is required.");
        }

        var input = request.Submit ? request.Text + "\r" : request.Text;
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(input);
        if (byteCount > TerminalControlProtocol.MaxMessageBytes)
        {
            return new TerminalInputResponse(false, $"Input exceeds {TerminalControlProtocol.MaxMessageBytes} bytes.");
        }

        Terminal? terminal;
        string? sessionId;
        lock (s_lock)
        {
            terminal = s_terminal;
            sessionId = s_sessionId;
        }

        if (terminal == null || string.IsNullOrWhiteSpace(sessionId))
        {
            return new TerminalInputResponse(false, "No active terminal session.");
        }

        await TerminalIoRouter.RouteInputAsync(
            _stateService,
            terminal,
            sessionId,
            input,
            TerminalIoSource.AgentTool,
            cancellationToken);

        return new TerminalInputResponse(true, "Input sent.", SessionId: sessionId);
    }

    public void RegisterExternalTerminal(Terminal terminal, string sessionId, string workingDirectory)
    {
        s_lifecycleGate.Wait();
        try
        {
            lock (s_lock)
            {
                if (s_terminal != null)
                    throw new InvalidOperationException("A terminal session is already active");
                s_terminal = terminal;
                s_sessionId = sessionId;
                s_cli = null; // external sessions don't report a CLI; web-side per-CLI behavior stays off
                s_workingDirectory = workingDirectory;
                s_sessionOwnerId = BuildSessionOwnerId(sessionId);
                s_externallyOwned = true;
            }
        }
        finally
        {
            s_lifecycleGate.Release();
        }
        _localClientTracker.AcquireOwner(BuildSessionOwnerId(sessionId));
    }

    public async Task UnregisterTerminalAsync()
    {
        await DetachExternalSessionAsync("CLI session ended");
    }

    public async Task StopSessionAsync()
    {
        lock (s_lock)
        {
            if (s_externallyOwned)
                return;
        }

        await ShutdownActiveSessionAsync(
            expectedTerminal: null,
            exitCode: 0,
            closeReason: "Terminal session stopped",
            requireExternalOwnership: false);
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

    public async Task<bool> SendRemoteCommandAsync(string command, string? payload = null, CancellationToken cancellationToken = default)
    {
        string? sessionId;
        lock (s_lock)
        {
            sessionId = s_sessionId;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        return await _stateService.SendRemoteCommandAsync(sessionId, command, payload, cancellationToken);
    }

    public Task<TerminalSnapshot?> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        Terminal? terminal;
        string? sessionId;
        lock (s_lock)
        {
            terminal = s_terminal;
            sessionId = s_sessionId;
        }

        if (terminal == null || sessionId == null)
            return Task.FromResult<TerminalSnapshot?>(null);

        var capture = terminal.CaptureSnapshotData();
        var snapshot = new TerminalSnapshot(
            sessionId,
            DateTimeOffset.UtcNow,
            capture.Cols,
            capture.Rows,
            capture.ScreenText,
            new TerminalXtermUiBytes(
                ContentType: "application/vnd.viberails.xterm-ui-bytes",
                Encoding: "base64",
                Format: "ansi-replay",
                Base64: Convert.ToBase64String(capture.XtermReplayBytes),
                ByteLength: capture.XtermReplayBytes.Length,
                Cols: capture.Cols,
                Rows: capture.Rows,
                IncludesScrollback: false,
                RendererHint: "xterm.js"),
            XtermPngString: null);

        return Task.FromResult<TerminalSnapshot?>(snapshot);
    }

    public Task<TerminalImageCaptureResult> CaptureImageSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TerminalImageCaptureResult(
            Success: false,
            PngBytes: null,
            Message: "Server-side terminal image capture is not implemented yet. Use replay text/ANSI snapshot for now."));
    }

    private static async Task WebSocketInputLoopAsync(
        Terminal terminal,
        WebSocket webSocket,
        ITerminalConsumer snapshotConsumer,
        ITerminalStateService stateService,
        string sessionId,
        CancellationToken ct)
    {
        var buffer = new byte[4096];
        var lastReplayUtc = DateTimeOffset.MinValue;
        var minReplayInterval = TimeSpan.FromMilliseconds(250);
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
                        TerminalResizeCoordinator.ApplyResize(
                            terminal,
                            stateService,
                            sessionId,
                            cols,
                            rows,
                            TerminalIoSource.LocalWebUi);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "[Terminal] Failed to resize PTY to {Cols}x{Rows}", cols, rows);
                    }
                    continue;
                }

                // Reserved browser command: refresh the viewer from the server-side
                // emulator snapshot without sending anything to the PTY. This avoids
                // SIGWINCH/Ctrl+L redraws that make full-screen TUIs print duplicate
                // frames after reconnect. Throttled per-socket so a runaway client
                // can't spin the subscriber lock.
                if (result.MessageType == WebSocketMessageType.Text &&
                    TerminalControlProtocol.IsReplayCommand(input))
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - lastReplayUtc >= minReplayInterval)
                    {
                        lastReplayUtc = now;
                        terminal.PushSnapshotTo(snapshotConsumer);
                    }
                    continue;
                }

                // A reserved control frame that reached this point failed to parse —
                // a bug on the sending side, not something the user typed. Dropping
                // it is the only safe move: routing would write the literal frame to
                // PTY stdin as keystrokes. See runbooks/terminal/TERMINAL.md
                // "## 2026-07-26 __resize__:171,4 typed into the OpenCode composer".
                if (result.MessageType == WebSocketMessageType.Text &&
                    TerminalControlProtocol.IsReservedControlFrame(input))
                {
                    Log.Warning(
                        "[Terminal] Dropped malformed control frame from viewer: {Frame}",
                        TerminalControlProtocol.SanitizeFrameForLog(input));
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

    /// <summary>
    /// Sends __LOCKED__ to the connecting viewer and waits for them to respond with
    /// __PIN__:&lt;digits&gt;. Allows up to 3 attempts before closing the socket.
    /// Returns true if PIN was verified, false if the connection should be dropped.
    /// </summary>
    private static async Task<bool> PinChallengeAsync(WebSocket webSocket, CancellationToken ct)
    {
        const int maxAttempts = 3;
        var lockedBytes = System.Text.Encoding.UTF8.GetBytes(TerminalControlProtocol.Locked);
        var buffer = new byte[256];

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Send __LOCKED__
            try
            {
                await webSocket.SendAsync(lockedBytes, WebSocketMessageType.Text, true, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[PIN] Failed to send LOCKED challenge");
                return false;
            }

            // Wait for __PIN__:<digits>
            string response;
            try
            {
                var result = await webSocket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return false;

                response = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count).Trim();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[PIN] Failed to receive PIN response");
                return false;
            }

            // Validate format: __PIN__:<4-6 digits>
            if (!response.StartsWith(TerminalControlProtocol.PinResponse, StringComparison.Ordinal))
            {
                Log.Warning("[PIN] Bad PIN message format, attempt {Attempt}/{Max}", attempt + 1, maxAttempts);
                continue;
            }

            var pin = response[TerminalControlProtocol.PinResponse.Length..].Trim();
            if (pin.Length < 4 || pin.Length > 6 || !pin.All(char.IsDigit))
            {
                Log.Warning("[PIN] PIN out of range ({Len} chars), attempt {Attempt}/{Max}", pin.Length, attempt + 1, maxAttempts);
                continue;
            }

            if (RemoteConfig.VerifyPin(pin))
            {
                Log.Information("[PIN] PIN verified successfully");
                return true;
            }

            Log.Warning("[PIN] Incorrect PIN, attempt {Attempt}/{Max}", attempt + 1, maxAttempts);
        }

        // Exhausted attempts — disconnect
        Log.Warning("[PIN] Too many failed PIN attempts, closing connection");
        try
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Too many failed PIN attempts", CancellationToken.None);
        }
        catch { }

        return false;
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

    private async Task ShutdownActiveSessionAsync(
        Terminal? expectedTerminal,
        int exitCode,
        string closeReason,
        bool requireExternalOwnership)
    {
        await s_lifecycleGate.WaitAsync();
        try
        {
            var teardown = CaptureSessionForTeardown(expectedTerminal, requireExternalOwnership, detachOnly: false);
            if (teardown == null)
                return;

            if (!string.IsNullOrEmpty(teardown.OwnerId))
            {
                _localClientTracker.ReleaseOwner(teardown.OwnerId);
            }

            await CloseWebSocketAsync(teardown.ActiveWebSocket, closeReason);

            if (teardown.TerminalToDispose != null)
            {
                await teardown.TerminalToDispose.DisposeAsync();
            }

            TerminalResizeCoordinator.ClearSession(teardown.SessionId);
            await _stateService.CompleteSessionAsync(teardown.SessionId, exitCode);
        }
        finally
        {
            s_lifecycleGate.Release();
        }
    }

    private async Task DetachExternalSessionAsync(string closeReason)
    {
        await s_lifecycleGate.WaitAsync();
        try
        {
            var teardown = CaptureSessionForTeardown(expectedTerminal: null, requireExternalOwnership: true, detachOnly: true);
            if (teardown == null)
                return;

            if (!string.IsNullOrEmpty(teardown.OwnerId))
            {
                _localClientTracker.ReleaseOwner(teardown.OwnerId);
            }

            TerminalResizeCoordinator.ClearSession(teardown.SessionId);
            await CloseWebSocketAsync(teardown.ActiveWebSocket, closeReason);
        }
        finally
        {
            s_lifecycleGate.Release();
        }
    }

    private void ScheduleExitCleanup(Terminal terminal, string sessionId, int exitCode)
    {
        Log.Information(
            "[Terminal] Session exit detected. sessionId={SessionId} exitCode={ExitCode} exitCodeHex=0x{ExitCodeHex:X8} pid={Pid}",
            sessionId, exitCode, (uint)exitCode, Environment.ProcessId);
        _ = Task.Run(async () =>
        {
            try
            {
                await ShutdownActiveSessionAsync(
                    expectedTerminal: terminal,
                    exitCode: exitCode,
                    closeReason: "Terminal session exited",
                    requireExternalOwnership: false);

                // The browser-tab path's end of a session. Deliberately outside
                // ShutdownActiveSessionAsync: that method holds s_lifecycleGate, and a post-exit
                // step can legitimately run for minutes. No-ops unless the environment registered
                // post-exit steps, and is idempotent with TerminalRunner.CompleteSessionAsync.
                await _runner.RunPostStepsAsync(sessionId, exitCode, CancellationToken.None);

                Log.Information("[Terminal] Session exit cleanup complete. sessionId={SessionId}", sessionId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Terminal] Exit cleanup failed for session {SessionId}", sessionId);
            }
        });
    }

    private SessionTeardownState? CaptureSessionForTeardown(
        Terminal? expectedTerminal,
        bool requireExternalOwnership,
        bool detachOnly)
    {
        lock (s_lock)
        {
            if (s_terminal == null || s_sessionId == null)
                return null;

            if (expectedTerminal != null && !ReferenceEquals(s_terminal, expectedTerminal))
                return null;

            if (requireExternalOwnership && !s_externallyOwned)
                return null;

            var teardown = new SessionTeardownState(
                SessionId: s_sessionId,
                OwnerId: s_sessionOwnerId,
                ActiveWebSocket: s_activeWebSocket,
                TerminalToDispose: (!detachOnly && !s_externallyOwned) ? s_terminal : null);

            s_terminal = null;
            s_sessionId = null;
            s_cli = null;
            s_workingDirectory = null;
            s_sessionOwnerId = null;
            s_activeWebSocket = null;
            s_externallyOwned = false;
            return teardown;
        }
    }

    private static async Task CloseWebSocketAsync(WebSocket? webSocket, string reason)
    {
        if (webSocket?.State != WebSocketState.Open)
            return;

        try
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
        }
        catch (Exception)
        {
            // Best-effort close only.
        }
    }

    private static string BuildSessionOwnerId(string sessionId)
        => $"terminal-session:{sessionId}";

    private sealed record SessionTeardownState(
        string SessionId,
        string? OwnerId,
        WebSocket? ActiveWebSocket,
        Terminal? TerminalToDispose);

}
