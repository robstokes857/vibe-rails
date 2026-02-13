using System.Net.WebSockets;
using System.Text;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Opens a ClientWebSocket to the VibeRails server's ws/v1/terminal endpoint.
/// Sends PTY output as binary frames, receives browser input as text (UTF-8 keystrokes).
/// </summary>
public sealed class RemoteTerminalConnection : IRemoteTerminalConnection
{
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    public event Action<byte[]>? OnInputReceived;
    public event Action? OnReplayRequested;
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public async Task ConnectAsync(string sessionId, CancellationToken ct)
    {
        var frontendUrl = ParserConfigs.GetFrontendUrl();
        var apiKey = ParserConfigs.GetApiKey();

        if (string.IsNullOrWhiteSpace(frontendUrl) || string.IsNullOrWhiteSpace(apiKey))
            return;

        var wsUri = frontendUrl
            .Replace("https://", "wss://")
            .Replace("http://", "ws://");
        if (!wsUri.StartsWith("ws://") && !wsUri.StartsWith("wss://"))
            wsUri = "wss://" + wsUri;
        wsUri = wsUri.TrimEnd('/') + $"/ws/v1/terminal?sessionId={Uri.EscapeDataString(sessionId)}";

        _socket = new ClientWebSocket();
        _socket.Options.SetRequestHeader("X-Api-Key", apiKey);

        _cts = new CancellationTokenSource();

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            await _socket.ConnectAsync(new Uri(wsUri), connectCts.Token);

            Console.WriteLine($"[Remote] WebSocket connected to server for session {sessionId[..8]}...");
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Remote] Failed to connect WebSocket: {ex.Message}");
            _socket.Dispose();
            _socket = null;
        }
    }

    public async Task SendOutputAsync(ReadOnlyMemory<byte> data)
    {
        if (_socket?.State != WebSocketState.Open)
            return;

        try
        {
            await _socket.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Remote] Send error: {ex.Message}");
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
                    break;

                if (result.MessageType == WebSocketMessageType.Text || result.MessageType == WebSocketMessageType.Binary)
                {
                    // Accumulate full message if fragmented
                    byte[] inputBytes;
                    if (result.EndOfMessage)
                    {
                        inputBytes = buffer[..result.Count].ToArray();
                    }
                    else
                    {
                        using var ms = new System.IO.MemoryStream();
                        ms.Write(buffer, 0, result.Count);
                        while (!result.EndOfMessage)
                        {
                            result = await _socket.ReceiveAsync(buffer, ct);
                            ms.Write(buffer, 0, result.Count);
                        }
                        inputBytes = ms.ToArray();
                    }

                    // Check for replay command from server (sent when browser connects)
                    if (result.MessageType == WebSocketMessageType.Text &&
                        Encoding.UTF8.GetString(inputBytes) == "__replay__")
                    {
                        OnReplayRequested?.Invoke();
                        continue;
                    }

                    OnInputReceived?.Invoke(inputBytes);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Remote] Receive loop error: {ex.Message}");
        }

        Console.WriteLine("[Remote] WebSocket receive loop ended");
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        if (_socket?.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
            }
            catch { }
        }

        if (_receiveLoop != null)
        {
            try { await _receiveLoop; } catch { }
        }

        _socket?.Dispose();
        _cts?.Dispose();
    }
}
