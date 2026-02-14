using System.Net.WebSockets;
using System.Threading.Channels;

namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// Sends PTY output to a WebSocket as binary frames. Used by the Web UI terminal path.
/// Uses a single-writer queue so output frames are sent in-order without concurrent SendAsync calls.
/// </summary>
public sealed class WebSocketConsumer : ITerminalConsumer, IDisposable
{
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly WebSocket _webSocket;
    private readonly CancellationToken _ct;
    private readonly Task _sendLoop;

    public WebSocketConsumer(WebSocket webSocket, CancellationToken ct)
    {
        _webSocket = webSocket;
        _ct = ct;
        _sendLoop = Task.Run(() => SendLoopAsync());
    }

    public void OnOutput(ReadOnlyMemory<byte> data)
    {
        if (_webSocket.State != WebSocketState.Open || _ct.IsCancellationRequested)
            return;

        _outbound.Writer.TryWrite(data.ToArray());
    }

    public void Dispose()
    {
        _outbound.Writer.TryComplete();
    }

    private async Task SendLoopAsync()
    {
        try
        {
            while (await _outbound.Reader.WaitToReadAsync(_ct))
            {
                while (_outbound.Reader.TryRead(out var frame))
                {
                    if (_webSocket.State != WebSocketState.Open)
                        return;

                    await _webSocket.SendAsync(frame, WebSocketMessageType.Binary, true, _ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }
}
