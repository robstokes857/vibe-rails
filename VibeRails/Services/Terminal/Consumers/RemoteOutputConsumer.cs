namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// Sends PTY output to the remote VibeRails server via WebSocket.
/// Fire-and-forget to avoid blocking the terminal read loop.
/// Same pattern as WebSocketConsumer.
/// </summary>
public sealed class RemoteOutputConsumer : ITerminalConsumer
{
    private readonly IRemoteTerminalConnection _connection;
    private readonly Func<bool>? _canForward;

    public RemoteOutputConsumer(IRemoteTerminalConnection connection, Func<bool>? canForward = null)
    {
        _connection = connection;
        _canForward = canForward;
    }

    public void OnOutput(ReadOnlyMemory<byte> data)
    {
        if (_canForward != null && !_canForward())
            return;

        if (!_connection.IsConnected) return;
        // SendOutputAsync copies before enqueueing, so this remains safe even
        // though data points to Terminal's shared read buffer.
        _ = _connection.SendOutputAsync(data);
    }
}
