namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// Sends PTY output to the remote VibeRails server via WebSocket.
/// Fire-and-forget to avoid blocking the terminal read loop.
/// Same pattern as WebSocketConsumer.
/// </summary>
public sealed class RemoteOutputConsumer : ITerminalConsumer
{
    private readonly IRemoteTerminalConnection _connection;

    public RemoteOutputConsumer(IRemoteTerminalConnection connection)
    {
        _connection = connection;
    }

    public void OnOutput(ReadOnlyMemory<byte> data)
    {
        if (!_connection.IsConnected) return;
        // Must copy: data points to Terminal's shared read buffer which gets
        // reused on the next read. Without copying, the async send races with
        // the next buffer fill, causing corrupted output (random characters).
        _ = _connection.SendOutputAsync(data.ToArray());
    }
}
