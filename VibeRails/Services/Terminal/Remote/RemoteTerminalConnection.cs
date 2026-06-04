using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Serilog;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Opens a ClientWebSocket to the VibeRails server's ws/v1/terminal endpoint.
/// Sends PTY output as binary frames, receives browser input as text (UTF-8 keystrokes).
///
/// The connection is supervised: if the socket drops unexpectedly (network blip,
/// idle-timeout at a proxy, etc.) a background loop reconnects with exponential
/// backoff using the same sessionId, and raises <see cref="OnReconnected"/> so the
/// session can re-lock / re-snapshot. Only <see cref="DisposeAsync"/> stops it.
/// </summary>
public sealed class RemoteTerminalConnection : IRemoteTerminalConnection
{
    private const int MaxBufferedFrames = 256;

    // Send WS ping frames well inside the ~2 minute idle window observed on the
    // viberails.ai relay so dumb proxies/load balancers don't drop an idle socket.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    private readonly Channel<OutboundFrame> _outbound = Channel.CreateBounded<OutboundFrame>(
        new BoundedChannelOptions(MaxBufferedFrames)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private volatile ClientWebSocket? _socket;
    private string? _sessionId;
    // Cancelled only by DisposeAsync; survives across reconnects.
    private CancellationTokenSource? _lifetimeCts;
    private Task? _supervisor;
    private int _overflowTriggered;
    private int _resyncRequested;
    private int _disposed;

    public event Action<byte[]>? OnInputReceived;
    public event Action? OnReplayRequested;
    public event Action? OnBrowserDisconnected;
    public event Action? OnReconnected;
    public event Action<int, int>? OnResizeRequested;
    public event Action<string, string?>? OnCommandReceived;
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(string sessionId, CancellationToken ct)
    {
        var frontendUrl = ParserConfigs.GetFrontendUrl();
        var apiKey = ParserConfigs.GetApiKey();

        if (string.IsNullOrWhiteSpace(frontendUrl) || string.IsNullOrWhiteSpace(apiKey))
            return;

        _sessionId = sessionId;
        _lifetimeCts = new CancellationTokenSource();

        // First attempt is synchronous so the caller can gate session wiring on
        // IsConnected. If it fails, behaviour is unchanged: caller disposes us.
        if (!await TryConnectSocketAsync(ct))
            return;

        _supervisor = Task.Run(() => SuperviseAsync(_lifetimeCts.Token));
    }

    private async Task<bool> TryConnectSocketAsync(CancellationToken ct)
    {
        var frontendUrl = ParserConfigs.GetFrontendUrl();
        var apiKey = ParserConfigs.GetApiKey();
        if (string.IsNullOrWhiteSpace(frontendUrl) || string.IsNullOrWhiteSpace(apiKey) || _sessionId is null)
            return false;

        var wsUri = frontendUrl
            .Replace("https://", "wss://")
            .Replace("http://", "ws://");
        if (!wsUri.StartsWith("ws://") && !wsUri.StartsWith("wss://"))
            wsUri = "wss://" + wsUri;
        wsUri = wsUri.TrimEnd('/') + $"/ws/v1/terminal?sessionId={Uri.EscapeDataString(_sessionId)}";

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Api-Key", apiKey);
        socket.Options.KeepAliveInterval = KeepAliveInterval;

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new Uri(wsUri), connectCts.Token);

            _socket = socket;
            Volatile.Write(ref _overflowTriggered, 0);
            Log.Information("[Remote] WebSocket connected to server for session {SessionId}...", _sessionId[..8]);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Remote] Failed to connect WebSocket");
            socket.Dispose();
            return false;
        }
    }

    /// <summary>
    /// Runs the send/receive loops for the current socket; when they exit (the
    /// connection broke) reconnects with backoff until the lifetime is cancelled.
    /// </summary>
    private async Task SuperviseAsync(CancellationToken lifetime)
    {
        var attempt = 0;

        while (!lifetime.IsCancellationRequested)
        {
            var socket = _socket;
            if (socket is null)
                break;

            using (var connCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime))
            {
                var send = SendLoopAsync(socket, connCts.Token);
                var receive = ReceiveLoopAsync(socket, connCts.Token);

                // Either loop completing means this socket is finished.
                await Task.WhenAny(send, receive);
                connCts.Cancel();
                try { await Task.WhenAll(send, receive); } catch { }
            }

            try { socket.Dispose(); } catch { }
            if (ReferenceEquals(_socket, socket))
                _socket = null;

            if (lifetime.IsCancellationRequested)
                break;

            // Connection dropped mid-session. Discard any stale queued frames —
            // a snapshot re-push on reconnect rebuilds the screen from ground truth.
            while (_outbound.Reader.TryRead(out _)) { }

            var reconnected = false;
            while (!lifetime.IsCancellationRequested)
            {
                attempt++;
                var delay = BackoffDelay(attempt);
                Log.Warning(
                    "[Remote] Connection lost; reconnecting in {Delay:0.0}s (attempt {Attempt})",
                    delay.TotalSeconds, attempt);

                try { await Task.Delay(delay, lifetime); }
                catch (OperationCanceledException) { break; }

                if (await TryConnectSocketAsync(lifetime))
                {
                    attempt = 0;
                    reconnected = true;
                    Log.Information("[Remote] Reconnected for session {SessionId}", _sessionId![..8]);
                    break;
                }
            }

            if (!reconnected)
                break;

            try { OnReconnected?.Invoke(); }
            catch (Exception ex) { Log.Error(ex, "[Remote] OnReconnected handler failed"); }
        }

        Log.Information("[Remote] WebSocket supervisor ended");
    }

    private static TimeSpan BackoffDelay(int attempt)
    {
        // Exponential (1s, 2s, 4s, ...) capped, with jitter to avoid thundering herd.
        // attempt is 1-based (caller increments before calling), so subtract 1 to start at 1s.
        var seconds = Math.Min(MaxReconnectDelay.TotalSeconds, Math.Pow(2, Math.Min(attempt - 1, 6)));
        var jitter = Random.Shared.NextDouble() * 0.5 + 0.75; // 0.75x .. 1.25x
        return TimeSpan.FromSeconds(seconds * jitter);
    }

    public Task SendOutputAsync(ReadOnlyMemory<byte> data)
    {
        // Copy because caller memory may be reused by PTY read loop.
        TryQueueFrame(WebSocketMessageType.Binary, data.ToArray());
        return Task.CompletedTask;
    }

    public Task SendControlAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return Task.CompletedTask;

        TryQueueFrame(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(message));
        return Task.CompletedTask;
    }

    public Task SendCommandAsync(string command, string? payload = null)
    {
        var message = TerminalControlProtocol.BuildCommand(command, payload);
        return SendControlAsync(message);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text || result.MessageType == WebSocketMessageType.Binary)
                {
                    // Accumulate full message if fragmented
                    byte[] inputBytes;
                    if (result.EndOfMessage)
                    {
                        if (result.Count > TerminalControlProtocol.MaxMessageBytes)
                        {
                            Log.Warning("[Remote] Inbound message exceeded limit ({Bytes} bytes)", result.Count);
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
                            Log.Warning("[Remote] Inbound fragmented message exceeded limit ({Bytes} bytes)", ms.Length);
                            break;
                        }
                        while (!result.EndOfMessage)
                        {
                            result = await socket.ReceiveAsync(buffer, ct);
                            ms.Write(buffer, 0, result.Count);
                            if (ms.Length > TerminalControlProtocol.MaxMessageBytes)
                            {
                                Log.Warning("[Remote] Inbound fragmented message exceeded limit ({Bytes} bytes)", ms.Length);
                                break;
                            }
                        }
                        if (ms.Length > TerminalControlProtocol.MaxMessageBytes)
                            break;
                        inputBytes = ms.ToArray();
                    }

                    // Check for control commands from server
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var text = Encoding.UTF8.GetString(inputBytes);
                        if (TerminalControlProtocol.IsReplayCommand(text))
                        {
                            OnReplayRequested?.Invoke();
                            continue;
                        }
                        if (text == TerminalControlProtocol.BrowserDisconnectedCommand)
                        {
                            OnBrowserDisconnected?.Invoke();
                            continue;
                        }
                        if (TerminalControlProtocol.TryParseResizeCommand(text, out var cols, out var rows))
                        {
                            OnResizeRequested?.Invoke(cols, rows);
                            continue;
                        }
                        if (TerminalControlProtocol.TryParseCommand(text, out var command, out var payload))
                        {
                            OnCommandReceived?.Invoke(command, payload);
                            continue;
                        }
                    }

                    OnInputReceived?.Invoke(inputBytes);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "[Remote] Receive loop error");
        }

        Log.Information("[Remote] WebSocket receive loop ended");
    }

    private void TryQueueFrame(WebSocketMessageType messageType, byte[] payload)
    {
        if (_socket?.State != WebSocketState.Open || payload.Length == 0)
            return;

        if (!_outbound.Writer.TryWrite(new OutboundFrame(payload, messageType)))
        {
            TriggerOverflowResync();
        }
    }

    private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        try
        {
            while (await _outbound.Reader.WaitToReadAsync(ct))
            {
                if (Volatile.Read(ref _resyncRequested) == 1)
                {
                    DrainAndRequestResync();
                    continue;
                }

                while (_outbound.Reader.TryRead(out var frame))
                {
                    if (socket.State != WebSocketState.Open)
                        return;

                    // An overflow asked us to resync: drop the rest of the (stale, oversized)
                    // backlog and rebuild the screen from a fresh snapshot instead. Draining
                    // happens here in the single reader so it can't race the writers.
                    if (Volatile.Read(ref _resyncRequested) == 1)
                    {
                        DrainAndRequestResync();
                        break;
                    }

                    await socket.SendAsync(frame.Payload, frame.MessageType, true, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            Log.Error(ex, "[Remote] Send loop error");
        }
    }

    private void DrainAndRequestResync()
    {
        while (_outbound.Reader.TryRead(out _)) { }
        Volatile.Write(ref _resyncRequested, 0);
        Volatile.Write(ref _overflowTriggered, 0);

        // Reuse the replay path: it re-pushes a ground-truth snapshot WITHOUT touching the
        // viewer's auth state (no re-lock), which is exactly what an overflow resync needs.
        try { OnReplayRequested?.Invoke(); }
        catch (Exception ex) { Log.Error(ex, "[Remote] Overflow resync handler failed"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _lifetimeCts?.Cancel();
        _outbound.Writer.TryComplete();

        var socket = _socket;
        if (socket?.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
            }
            catch { }
        }

        if (_supervisor != null)
        {
            try { await _supervisor; } catch { }
        }

        _socket?.Dispose();
        _lifetimeCts?.Dispose();
    }

    private sealed record OutboundFrame(byte[] Payload, WebSocketMessageType MessageType);

    private void TriggerOverflowResync()
    {
        if (Interlocked.Exchange(ref _overflowTriggered, 1) == 1)
            return;

        // Do NOT tear the socket down. Aborting forces a full reconnect + PIN re-lock and, under
        // output sustained faster than the relay drains, an endless abort/reconnect/re-lock loop.
        // Instead ask the send loop to drop the backlog and re-push a snapshot on the SAME
        // connection (viewer stays authorized). _overflowTriggered de-dupes until the send loop
        // clears it after resyncing.
        Log.Warning("[Remote] Output backlog overflowed; requesting snapshot resync");
        Volatile.Write(ref _resyncRequested, 1);
    }
}
