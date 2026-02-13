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
        _ = _connection.SendOutputAsync(data);
    }
}
