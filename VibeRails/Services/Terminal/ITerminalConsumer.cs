namespace VibeRails.Services.Terminal;

/// <summary>
/// Receives PTY output from a Terminal's read loop.
/// Implementations must be fast and non-blocking — the read loop dispatches synchronously.
/// </summary>
public interface ITerminalConsumer
{
    void OnOutput(ReadOnlyMemory<byte> data);
}
