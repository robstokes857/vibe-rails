using System.Globalization;
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
/// idle-timeout at a proxy, etc.) a background loop reconnects using the same sessionId
/// and raises <see cref="OnReconnected"/> so the session can re-lock / re-snapshot.
/// Each failed handshake is classified by its HTTP status (VIBE-157):
/// <list type="bullet">
/// <item>404: the relay no longer has this session (it reaps rows whose CLI has been gone a
/// while, including across a server restart). Retrying the same handshake can never succeed,
/// so the session is registered again and one more handshake is made.</item>
/// <item>400/401/403: the relay refused the key or the request. Retrying cannot fix that, so
/// reconnecting stops for this terminal.</item>
/// <item>No response, 429 or 5xx: the host is unavailable. The machine-wide
/// <see cref="RemoteRelayBackoff"/> paces these, so many terminals do not each probe it.</item>
/// </list>
/// Only <see cref="DisposeAsync"/> or a rejection stops the loop.
/// </summary>
public sealed class RemoteTerminalConnection : IRemoteTerminalConnection
{
    private const int MaxBufferedFrames = 256;

    // Send WS ping frames well inside the ~2 minute idle window observed on the
    // viberails.ai relay so dumb proxies/load balancers don't drop an idle socket.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    // Grows only for failures that belong to this session; host outages are paced by RemoteRelayBackoff.
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromMinutes(5);

    private readonly Func<CancellationToken, Task<bool>>? _reregister;
    private readonly RemoteRelayBackoff _hostBackoff;

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

    /// <summary>A connection that never re-registers its session; it uses the machine-wide relay backoff.</summary>
    public RemoteTerminalConnection() : this(null) { }

    /// <param name="reregister">
    /// Registers this session with the relay again after a 404 handshake and returns true when the
    /// relay accepted it. Supply it only for a session that was registered in the first place.
    /// </param>
    /// <param name="hostBackoff">Host-wide reconnect pacing; defaults to <see cref="RemoteRelayBackoff.Shared"/>.</param>
    public RemoteTerminalConnection(Func<CancellationToken, Task<bool>>? reregister, RemoteRelayBackoff? hostBackoff = null)
    {
        _reregister = reregister;
        _hostBackoff = hostBackoff ?? RemoteRelayBackoff.Shared;
    }

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
        // It does not wait on the host-wide backoff (one attempt per new terminal),
        // but its outcome still updates it.
        var result = await ConnectOnceAsync(ct);
        ReportToHostBackoff(result);
        if (result.Outcome != HandshakeOutcome.Connected)
            return;

        _supervisor = Task.Run(() => SuperviseAsync(_lifetimeCts.Token));
    }

    internal enum HandshakeOutcome { Connected, SessionMissing, Rejected, HostUnavailable, Cancelled }

    internal readonly record struct HandshakeResult(HandshakeOutcome Outcome, int StatusCode = 0, TimeSpan? RetryAfter = null);

    /// <summary>Maps a failed handshake's HTTP status (0 when there was no response) to what to do next.</summary>
    internal static HandshakeOutcome ClassifyFailure(int statusCode) => statusCode switch
    {
        404 => HandshakeOutcome.SessionMissing,
        400 or 401 or 403 => HandshakeOutcome.Rejected,
        // No response (DNS, refused, TLS, timeout), 429, 5xx, or a proxy's unexpected answer.
        _ => HandshakeOutcome.HostUnavailable
    };

    /// <summary>One handshake, plus a re-registration and a second handshake when the relay lost the session.</summary>
    private async Task<HandshakeResult> ConnectOnceAsync(CancellationToken ct)
    {
        var result = await TryConnectSocketAsync(ct);
        if (result.Outcome != HandshakeOutcome.SessionMissing || _reregister is null)
            return result;

        bool registered;
        try
        {
            registered = await _reregister(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(HandshakeOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Remote] Re-registering session {SessionId} failed", _sessionId![..8]);
            registered = false;
        }

        if (!registered)
        {
            Log.Warning("[Remote] Relay has no record of session {SessionId} and re-registering it did not succeed", _sessionId![..8]);
            return result;
        }

        Log.Information("[Remote] Re-registered session {SessionId} after the relay lost it (HTTP 404)", _sessionId![..8]);
        return await TryConnectSocketAsync(ct);
    }

    private void ReportToHostBackoff(HandshakeResult result)
    {
        switch (result.Outcome)
        {
            case HandshakeOutcome.HostUnavailable:
                _hostBackoff.ReportHostFailure(result.RetryAfter);
                break;
            case HandshakeOutcome.Connected or HandshakeOutcome.SessionMissing:
            case HandshakeOutcome.Rejected when result.StatusCode != 0:
                // Any HTTP answer from the relay proves the host itself is up.
                _hostBackoff.ReportHostAvailable();
                break;
        }
    }

    private async Task<HandshakeResult> TryConnectSocketAsync(CancellationToken ct)
    {
        var frontendUrl = ParserConfigs.GetFrontendUrl();
        var apiKey = ParserConfigs.GetApiKey();
        // Remote settings were cleared: nothing to connect to, and nothing a retry can change.
        if (string.IsNullOrWhiteSpace(frontendUrl) || string.IsNullOrWhiteSpace(apiKey) || _sessionId is null)
            return new(HandshakeOutcome.Rejected);

        var wsUri = frontendUrl
            .Replace("https://", "wss://")
            .Replace("http://", "ws://");
        if (!wsUri.StartsWith("ws://") && !wsUri.StartsWith("wss://"))
            wsUri = "wss://" + wsUri;
        wsUri = wsUri.TrimEnd('/') + $"/ws/v1/terminal?sessionId={Uri.EscapeDataString(_sessionId)}";

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Api-Key", apiKey);
        socket.Options.KeepAliveInterval = KeepAliveInterval;
        // Keeps the handshake's status and headers when the server answers anything but 101.
        socket.Options.CollectHttpResponseDetails = true;

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new Uri(wsUri), connectCts.Token);

            _socket = socket;
            Volatile.Write(ref _overflowTriggered, 0);
            Log.Information("[Remote] WebSocket connected to server for session {SessionId}...", _sessionId[..8]);
            return new(HandshakeOutcome.Connected);
        }
        catch (Exception ex)
        {
            var status = (int)socket.HttpStatusCode;
            var retryAfter = ParseRetryAfter(socket.HttpResponseHeaders, DateTimeOffset.UtcNow);
            socket.Dispose();
            if (ct.IsCancellationRequested)
                return new(HandshakeOutcome.Cancelled);

            var outcome = ClassifyFailure(status);
            if (ex is WebSocketException or OperationCanceledException or HttpRequestException)
            {
                // Expected while the relay is down or has lost the session: one line, no stack.
                Log.Warning("[Remote] WebSocket handshake failed for session {SessionId}: {Outcome}, HTTP {Status} ({Error})",
                    _sessionId[..8], outcome, status, ex.Message);
            }
            else
            {
                Log.Error(ex, "[Remote] Failed to connect WebSocket");
            }
            return new(outcome, status, retryAfter);
        }
    }

    /// <summary>Reads a Retry-After header in either form (delta-seconds or an HTTP date).</summary>
    internal static TimeSpan? ParseRetryAfter(IReadOnlyDictionary<string, IEnumerable<string>>? headers, DateTimeOffset now)
    {
        if (headers is null)
            return null;

        foreach (var (name, values) in headers)
        {
            if (!string.Equals(name, "Retry-After", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = values.FirstOrDefault()?.Trim();
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                return TimeSpan.FromSeconds(seconds);
            if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var date))
                return date > now ? date - now : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// Runs the send/receive loops for the current socket; when they exit (the
    /// connection broke) reconnects until the lifetime is cancelled or the relay
    /// rejects this terminal.
    /// </summary>
    private async Task SuperviseAsync(CancellationToken lifetime)
    {
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

            if (!await ReconnectAsync(lifetime))
                break;

            try { OnReconnected?.Invoke(); }
            catch (Exception ex) { Log.Error(ex, "[Remote] OnReconnected handler failed"); }
        }

        Log.Information("[Remote] WebSocket supervisor ended");
    }

    /// <summary>True once a new socket is open; false when cancelled or rejected.</summary>
    private async Task<bool> ReconnectAsync(CancellationToken lifetime)
    {
        var attempt = 0;
        // Only failures that belong to this session grow its own delay. While the host is
        // down, the shared RemoteRelayBackoff decides when anyone on this machine tries next.
        var sessionFailures = 0;
        while (!lifetime.IsCancellationRequested)
        {
            attempt++;
            var delay = BackoffDelay(sessionFailures + 1);
            Log.Warning(
                "[Remote] Connection lost; reconnecting in {Delay:0.0}s (attempt {Attempt})",
                delay.TotalSeconds, attempt);

            try
            {
                await Task.Delay(delay, lifetime);
                await _hostBackoff.WaitForTurnAsync(lifetime);
            }
            catch (OperationCanceledException) { return false; }

            var result = await ConnectOnceAsync(lifetime);
            ReportToHostBackoff(result);
            switch (result.Outcome)
            {
                case HandshakeOutcome.Connected:
                    Log.Information("[Remote] Reconnected for session {SessionId}", _sessionId![..8]);
                    return true;
                case HandshakeOutcome.Rejected:
                    Log.Warning(
                        "[Remote] Relay rejected session {SessionId} (HTTP {Status}); remote access for this terminal stays off until it is restarted",
                        _sessionId![..8], result.StatusCode);
                    return false;
                case HandshakeOutcome.SessionMissing:
                    sessionFailures++;
                    break;
            }
        }

        return false;
    }

    private static TimeSpan BackoffDelay(int attempt)
    {
        // Exponential (1s, 2s, 4s, ...) capped, with jitter to avoid thundering herd.
        // attempt is 1-based, so subtract 1 to start at 1s.
        var seconds = Math.Min(MaxReconnectDelay.TotalSeconds, Math.Pow(2, Math.Min(attempt - 1, 10)));
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
                        // The PIN handshake rides the input channel on this path: the
                        // verifier in TerminalRunner consumes __PIN__: frames downstream
                        // of OnInputReceived (and drops strays after authorization), so
                        // they must be forwarded, not treated as malformed — and never
                        // logged, because the payload is the user's PIN.
                        if (text.StartsWith(TerminalControlProtocol.PinResponse, StringComparison.Ordinal))
                        {
                            OnInputReceived?.Invoke(inputBytes);
                            continue;
                        }
                        // Reserved prefix that failed to parse: drop, never route.
                        // Routing types the literal frame into the TUI — same bug as
                        // the local path (`__resize__:171,4` typed into the TUI).
                        if (TerminalControlProtocol.IsReservedControlFrame(text))
                        {
                            Log.Warning(
                                "[Remote] Dropped malformed control frame: {Frame}",
                                TerminalControlProtocol.SanitizeFrameForLog(text));
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
                // Bounded: session completion awaits this, and a relay that never answers the
                // close frame (dead network, half-open socket) must not hold it forever.
                using var closeTimeout = new CancellationTokenSource(CloseTimeout);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", closeTimeout.Token);
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
