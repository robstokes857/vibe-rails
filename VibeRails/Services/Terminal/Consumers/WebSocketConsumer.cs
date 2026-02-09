using System.Net.WebSockets;

namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// Sends PTY output to a WebSocket as binary frames. Used by the Web UI terminal path.
/// Fire-and-forget send to avoid blocking the read loop on WebSocket backpressure.
/// </summary>
public sealed class WebSocketConsumer : ITerminalConsumer
{
    private readonly WebSocket _webSocket;
    private readonly CancellationToken _ct;

    public WebSocketConsumer(WebSocket webSocket, CancellationToken ct)
    {
        _webSocket = webSocket;
        _ct = ct;
    }

    public void OnOutput(ReadOnlyMemory<byte> data)
    {
        if (_webSocket.State != WebSocketState.Open) return;

        // Fire-and-forget: don't block the read loop on WebSocket throughput.
        // SendAsync is thread-safe for a single concurrent call, and we're the only producer.
        _ = _webSocket.SendAsync(data.ToArray(), WebSocketMessageType.Binary, true, _ct);
    }
}
