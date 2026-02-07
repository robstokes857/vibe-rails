using System.Net.WebSockets;
using System.Text;

namespace VibeRails.Services.Messaging;

/// <summary>
/// Bulletproof WebSocket messaging client with auto-connect, auto-reconnect,
/// and queued sends. Register as a singleton and inject anywhere.
///
/// Usage:
///   // Inject via DI (auto-configured with frontendUrl from app_config.json):
///   var client = serviceProvider.GetRequiredService&lt;MessagingClient&gt;();
///   client.OnMessageReceived += (sender, msg) => Console.WriteLine(msg);
///   await client.ConnectAsync();          // uses configured default URL
///   await client.SendAsync("hello");      // auto-connects if needed
///   await client.SendJsonAsync(json);     // send pre-serialized JSON
///
///   // Or connect to a custom URL:
///   await client.ConnectAsync("ws://other-server:1234/ws/messaging");
/// </summary>
public sealed class MessagingClient : IDisposable
{
    private readonly string? _defaultUri;
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private Task? _reconnectLoop;
    private string? _uri;
    private bool _disposed;

    /// <summary>
    /// Creates a MessagingClient with a pre-configured default server URL.
    /// </summary>
    public MessagingClient(string defaultUri)
    {
        _defaultUri = NormalizeToWsUri(defaultUri);
    }

    private static string NormalizeToWsUri(string uri)
    {
        uri = uri.Replace("https://", "wss://").Replace("http://", "ws://");
        if (!uri.StartsWith("ws://") && !uri.StartsWith("wss://"))
            uri = "ws://" + uri;
        if (!uri.Contains("/ws/messaging"))
            uri = uri.TrimEnd('/') + "/ws/messaging";
        return uri;
    }

    // Reconnect settings
    private const int InitialReconnectDelayMs = 500;
    private const int MaxReconnectDelayMs = 30_000;
    private const double ReconnectBackoffMultiplier = 2.0;

    // Send queue: messages queued while disconnected will be sent on reconnect
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _sendQueue = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Fired when a text message is received from the server.</summary>
    public event EventHandler<string>? OnMessageReceived;

    /// <summary>Fired when connection state changes.</summary>
    public event EventHandler<bool>? OnConnectionChanged;

    /// <summary>True if the WebSocket is currently connected and open.</summary>
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>
    /// Connect using the default URL from config. Safe to call multiple times.
    /// </summary>
    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (_defaultUri == null)
            throw new InvalidOperationException("No default URI configured. Use ConnectAsync(uri) instead.");
        return ConnectAsync(_defaultUri, ct);
    }

    /// <summary>
    /// Connect to a specific WebSocket server. Safe to call multiple times.
    /// If already connected, this is a no-op.
    /// </summary>
    public async Task ConnectAsync(string uri, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MessagingClient));
        _uri = NormalizeToWsUri(uri);

        if (IsConnected) return;

        await ConnectInternalAsync(ct);
    }

    /// <summary>
    /// Send a raw string message. Auto-connects if not connected.
    /// If connection fails, the message is queued and sent on reconnect.
    /// </summary>
    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MessagingClient));

        // Auto-connect on first send
        if (!IsConnected && _uri == null && _defaultUri != null)
        {
            _uri = _defaultUri;
            try
            {
                await ConnectInternalAsync(ct);
            }
            catch
            {
                // Connection failed — message will be queued below
            }
        }

        if (IsConnected)
        {
            await SendRawAsync(message, ct);
        }
        else
        {
            _sendQueue.Enqueue(message);
            // Make sure reconnect loop is running
            if (_uri != null) StartReconnectLoop();
        }
    }

    /// <summary>
    /// Send a pre-serialized JSON string. If disconnected, the message is queued.
    /// Use JsonSerializer.Serialize(payload, AppJsonSerializerContext.Default.YourType)
    /// for AOT-safe serialization before calling this.
    /// </summary>
    public Task SendJsonAsync(string json, CancellationToken ct = default)
    {
        return SendAsync(json, ct);
    }

    /// <summary>
    /// Gracefully disconnect. Can be reconnected later with ConnectAsync.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();

        if (_socket?.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None);
            }
            catch { }
        }

        // Wait for loops to finish
        if (_receiveLoop != null)
        {
            try { await _receiveLoop; } catch { }
        }
        if (_reconnectLoop != null)
        {
            try { await _reconnectLoop; } catch { }
        }

        _socket?.Dispose();
        _socket = null;
        _cts?.Dispose();
        _cts = null;

        OnConnectionChanged?.Invoke(this, false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();

        if (_socket != null)
        {
            try { _socket.Dispose(); } catch { }
        }

        _sendLock.Dispose();
    }

    // ────────────────────────── Internals ──────────────────────────

    private async Task ConnectInternalAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        _socket?.Dispose();
        _socket = new ClientWebSocket();

        try
        {
            await _socket.ConnectAsync(new Uri(_uri!), ct);
            OnConnectionChanged?.Invoke(this, true);

            // Drain any queued messages
            await DrainQueueAsync(_cts.Token);

            // Start the receive loop
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }
        catch
        {
            OnConnectionChanged?.Invoke(this, false);
            // Kick off background reconnect
            StartReconnectLoop();
        }
    }

    private void StartReconnectLoop()
    {
        if (_reconnectLoop != null && !_reconnectLoop.IsCompleted) return;
        _reconnectLoop = Task.Run(ReconnectLoopAsync);
    }

    private async Task ReconnectLoopAsync()
    {
        var delay = InitialReconnectDelayMs;

        while (!_disposed && _uri != null)
        {
            await Task.Delay(delay);

            if (IsConnected) return;

            try
            {
                _socket?.Dispose();
                _socket = new ClientWebSocket();

                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _socket.ConnectAsync(new Uri(_uri), connectCts.Token);

                OnConnectionChanged?.Invoke(this, true);

                // Reset backoff on success
                delay = InitialReconnectDelayMs;

                // Drain queued messages
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                await DrainQueueAsync(_cts.Token);

                // Restart receive loop
                _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
                return;
            }
            catch
            {
                // Exponential backoff
                delay = (int)Math.Min(delay * ReconnectBackoffMultiplier, MaxReconnectDelayMs);
            }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];

        try
        {
            while (_socket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    OnConnectionChanged?.Invoke(this, false);
                    StartReconnectLoop();
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message;
                    if (result.EndOfMessage)
                    {
                        message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    }
                    else
                    {
                        var sb = new StringBuilder(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        while (!result.EndOfMessage)
                        {
                            result = await _socket.ReceiveAsync(buffer, ct);
                            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        }
                        message = sb.ToString();
                    }

                    OnMessageReceived?.Invoke(this, message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (WebSocketException)
        {
            // Connection lost
        }
        catch
        {
            // Unexpected error
        }

        OnConnectionChanged?.Invoke(this, false);

        if (!_disposed && !ct.IsCancellationRequested)
        {
            StartReconnectLoop();
        }
    }

    private async Task SendRawAsync(string message, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_socket?.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            else
            {
                _sendQueue.Enqueue(message);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task DrainQueueAsync(CancellationToken ct)
    {
        while (_sendQueue.TryDequeue(out var queued) && IsConnected)
        {
            await SendRawAsync(queued, ct);
        }
    }
}
