namespace VibeRails.Services.Terminal;

public interface ISessionOutputWriter : IAsyncDisposable
{
    void Initialize(string sessionId, int cols = 120, int rows = 40);
    void Enqueue(byte[] payload);
    /// <summary>Append a recording-only message after all queued PTY output is flushed.</summary>
    void SetCompletionMessage(string message);
    void NotifyResize(int cols, int rows);
}
