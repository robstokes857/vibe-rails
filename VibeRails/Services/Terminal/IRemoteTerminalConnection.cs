namespace VibeRails.Services.Terminal;

/// <summary>
/// Contract for a remote WebSocket connection to the VibeRails server.
/// The CLI sends PTY output through this, and receives browser input back.
/// Designed as a seam for adding signing/command filtering later.
/// </summary>
public interface IRemoteTerminalConnection : IAsyncDisposable
{
    Task ConnectAsync(string sessionId, CancellationToken ct);
    Task SendOutputAsync(ReadOnlyMemory<byte> data);
    event Action<byte[]> OnInputReceived;
    event Action? OnReplayRequested;
    bool IsConnected { get; }
}
