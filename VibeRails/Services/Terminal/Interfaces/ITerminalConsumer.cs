namespace VibeRails.Services.Terminal;

/// <summary>
/// Receives PTY output from a Terminal's read loop.
/// Implementations must be fast and non-blocking — the read loop dispatches synchronously.
/// </summary>
public interface ITerminalConsumer
{
    void OnOutput(ReadOnlyMemory<byte> data);

    /// <summary>
    /// Called once after the PTY read loop ends, before <c>Exited</c> fires. Consumers that hold
    /// bytes back between <see cref="OnOutput"/> calls (to reassemble a split escape sequence, say)
    /// must flush them here, or a session's last few bytes are silently lost. Default is a no-op.
    /// </summary>
    void OnClosed() { }
}
