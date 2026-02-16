using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Serilog;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Opens a ClientWebSocket to the VibeRails server's ws/v1/terminal endpoint.
/// Sends PTY output as binary frames, receives browser input as text (UTF-8 keystrokes).
/// </summary>
public sealed class RemoteTerminalConnection : IRemoteTerminalConnection
{
    private readonly Channel<OutboundFrame> _outbound = Channel.CreateUnbounded<OutboundFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _sendLoop;
    private Task? _receiveLoop;

    public event Action<byte[]>? OnInputReceived;
    public event Action? OnReplayRequested;
    public event Action? OnBrowserDisconnected;
    public event Action<int, int>? OnResizeRequested;
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

            Log.Information("[Remote] WebSocket connected to server for session {SessionId}...", sessionId[..8]);
            _sendLoop = Task.Run(() => SendLoopAsync(_cts.Token));
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Remote] Failed to connect WebSocket");
            _socket.Dispose();
            _socket = null;
        }
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
                            result = await _socket.ReceiveAsync(buffer, ct);
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
                        if (text == TerminalControlProtocol.ReplayCommand)
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

        _outbound.Writer.TryWrite(new OutboundFrame(payload, messageType));
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _outbound.Reader.WaitToReadAsync(ct))
            {
                while (_outbound.Reader.TryRead(out var frame))
                {
                    if (_socket?.State != WebSocketState.Open)
                        return;

                    await _socket.SendAsync(frame.Payload, frame.MessageType, true, ct);
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

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _outbound.Writer.TryComplete();

        if (_socket?.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None);
            }
            catch { }
        }

        if (_sendLoop != null)
        {
            try { await _sendLoop; } catch { }
        }

        if (_receiveLoop != null)
        {
            try { await _receiveLoop; } catch { }
        }

        _socket?.Dispose();
        _cts?.Dispose();
    }

    private sealed record OutboundFrame(byte[] Payload, WebSocketMessageType MessageType);
}
